"""Offline tests for the optional journal classifier and BOSS recovery queue."""

import fcntl
import json
import os
from pathlib import Path
import subprocess
import tempfile
import time
import unittest


HERE = Path(__file__).resolve().parent


class MonitoringTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='xc2emby-monitor-test-')
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.events = self.root / 'events.json'
        self.marker = self.root / 'last-cache'
        self.called = self.root / 'called'
        self.lock = self.root / 'refresh.lock'
        refresh = self.root / 'refresh.sh'
        refresh.write_text('''#!/usr/bin/env bash
set -euo pipefail
[[ "$1" == --boss-only && "$IPTV_REFRESH_LOCK_FD" == 9 ]]
[[ /proc/$$/fd/9 -ef "$REFRESH_LOCK_FILE" ]]
flock -n 9
# A separate open must fail while the inherited descriptor still owns it.
if flock -n "$REFRESH_LOCK_FILE" -c true; then exit 99; fi
printf refreshed > "$CALLED_FILE"
''')
        fake_journal = self.root / 'journalctl'
        fake_journal.write_text('''#!/usr/bin/env bash
[[ ${JOURNAL_FAIL:-0} == 0 ]] || exit 1
cat "$EVENTS_FILE"
''')
        fake_journal.chmod(0o755)
        self.env = dict(os.environ, PATH=str(self.root) + ':' + os.environ['PATH'],
                        REFRESH_SCRIPT=str(refresh), REFRESH_LOCK_FILE=str(self.lock),
                        LAST_CACHE_SUCCESS_FILE=str(self.marker), EVENTS_FILE=str(self.events),
                        CALLED_FILE=str(self.called), LOCK_WAIT_SECONDS='2')
        self.set_events(0)

    def set_events(self, count, timestamp=None):
        timestamp = time.time() - 1 if timestamp is None else timestamp
        self.events.write_text('\n'.join(json.dumps({
            '__REALTIME_TIMESTAMP': str(int(timestamp * 1_000_000)),
            'MESSAGE': 'lineup-404 stream=xtream_live_42 reason=http-404',
        }) for _ in range(count)))

    def run_queue(self):
        return subprocess.run(['bash', str(HERE / 'boss-resync.sh')], env=self.env,
                              text=True, capture_output=True, timeout=8)

    def test_refresh_keeps_one_lock_through_child_execution(self):
        self.set_events(6)
        result = self.run_queue()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertTrue(self.called.exists())
        with self.lock.open('a') as lock:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)

    def test_recent_success_covers_old_failures(self):
        self.set_events(6, time.time() - 30)
        self.marker.write_text(str(int(time.time()) - 5) + '\n')
        result = self.run_queue()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn('No additional refresh needed', result.stdout)
        self.assertFalse(self.called.exists())

    def test_old_events_do_not_trigger_a_new_refresh(self):
        self.set_events(6, time.time() - 600)
        result = self.run_queue()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertFalse(self.called.exists())

    def test_waits_for_owner_then_rechecks_new_success(self):
        self.set_events(6, time.time() - 10)
        with self.lock.open('a') as lock:
            fcntl.flock(lock, fcntl.LOCK_EX)
            child = subprocess.Popen(['bash', str(HERE / 'boss-resync.sh')], env=self.env,
                                     text=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
            try:
                time.sleep(0.15)
                self.assertIsNone(child.poll(), 'the queued request must wait for the current refresh')
                self.marker.write_text(str(int(time.time())) + '\n')
                fcntl.flock(lock, fcntl.LOCK_UN)
                stdout, stderr = child.communicate(timeout=8)
                self.assertEqual(child.returncode, 0, stderr)
                self.assertIn('No additional refresh needed', stdout)
                self.assertFalse(self.called.exists())
            finally:
                if child.poll() is None:
                    child.kill()
                    child.communicate()

    def test_waits_then_recovers_if_fresh_failures_remain(self):
        self.set_events(6)
        with self.lock.open('a') as lock:
            fcntl.flock(lock, fcntl.LOCK_EX)
            child = subprocess.Popen(['bash', str(HERE / 'boss-resync.sh')], env=self.env,
                                     text=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
            try:
                time.sleep(0.15)
                self.assertIsNone(child.poll())
                fcntl.flock(lock, fcntl.LOCK_UN)
                _, stderr = child.communicate(timeout=8)
                self.assertEqual(child.returncode, 0, stderr)
                self.assertTrue(self.called.exists())
            finally:
                if child.poll() is None:
                    child.kill()
                    child.communicate()

    def test_busy_lock_is_never_deleted_or_bypassed(self):
        self.set_events(6)
        self.env['LOCK_WAIT_SECONDS'] = '1'
        with self.lock.open('a') as lock:
            original_inode = os.fstat(lock.fileno()).st_ino
            fcntl.flock(lock, fcntl.LOCK_EX)
            result = self.run_queue()
            self.assertEqual(result.returncode, 75, result.stderr)
            self.assertEqual(self.lock.stat().st_ino, original_inode)
            self.assertFalse(self.called.exists())

    def test_journal_error_is_not_treated_as_recovery(self):
        self.env['JOURNAL_FAIL'] = '1'
        self.assertNotEqual(self.run_queue().returncode, 0)
        self.assertFalse(self.called.exists())

    def test_stream_classifier_ignores_generic_cancellation_and_idle_stalls(self):
        prefix = 'Warn XtreamTunerHost: [live-fanout] Upstream interrupted for stream xtream_live_42: '
        retry = '; reconnecting in 500ms (1/3)'
        messages = [
            'Xtream live upstream read stalled: reason=read-stall idleMs=15001 timeoutMs=15000 activeViewers=1',
            'Xtream live upstream read stalled: reason=read-stall idleMs=15001 timeoutMs=15000 activeViewers=0',
            'The operation was canceled.',
            'Xtream live source ended unexpectedly',
            'The SSL connection could not be established',
            'The request was canceled due to the configured HttpClient.Timeout of 10 seconds elapsing.',
            'Response status code does not indicate success: 500 (Internal Server Error).',
            'Response status code does not indicate success: 404 (Not Found).',
        ]
        fixture = self.root / 'emby.log'
        fixture.write_text('\n'.join(prefix + message + retry for message in messages) + '\n')
        result = subprocess.run(['bash', str(HERE / 'emby-iptv-stream-monitor.sh'),
                                 '--test-input', str(fixture)], text=True, capture_output=True, check=True)
        reasons = [line.rsplit('reason=', 1)[1] for line in result.stdout.splitlines()]
        self.assertEqual(reasons, ['read-stall', 'eof', 'tls', 'timeout', 'http-5xx', 'http-404'])
        self.assertTrue(result.stdout.splitlines()[-1].startswith('lineup-404 '))


if __name__ == '__main__':
    unittest.main()
