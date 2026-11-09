'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

// Exercise the real AMD module's renderers without creating an Emby session.
// Test-only exports are added in memory; the shipped settings module is unchanged.
const source = fs.readFileSync(path.join(__dirname, '../Emby.Xtream.Plugin/Configuration/Web/config.js'), 'utf8').replace(/\r\n/g, '\n');
assert.ok(source.includes('    return View;\n});'), 'module export seam changed');
const instrumented = source.replace('    return View;\n});',
    '    return { getSyncProgressDisplay, renderProgressBar, renderDashboardHistory };\n});');
const htmlEscape = text => String(text).replace(/&/g, '&amp;').replace(/</g, '&lt;')
    .replace(/>/g, '&gt;').replace(/"/g, '&quot;').replace(/'/g, '&#39;');
let renderers;
vm.runInNewContext(instrumented, {
    define: (_deps, factory) => { renderers = factory(function BaseView() {}, {}); },
    document: {
        createTextNode: text => ({ text: String(text) }),
        createElement: () => ({
            text: '',
            appendChild(node) { this.text += node.text; },
            get innerHTML() { return htmlEscape(this.text); }
        })
    }
});

function historyHtml(entry) {
    const container = { innerHTML: '' };
    renderers.renderDashboardHistory({
        querySelector: selector => selector === '.dashboardHistoryContent' ? container : null
    }, { History: [Object.assign({
        StartTime: '2026-11-09T17:11:02Z', EndTime: '2026-11-09T17:18:09Z',
        WasMovieSync: true, MoviesTotal: 0, MoviesFailed: 0
    }, entry)] });
    return container.innerHTML;
}

const tests = [
    ['preparation uses its own counter instead of completed writes', () => {
        const display = renderers.getSyncProgressDisplay({ IsPreparing: true, Prepared: 50, PreparationTotal: 100, Completed: 0, Total: 92580 });
        assert.equal(display.percent, 50);
        assert.ok(display.detail.includes('50 / 100 prepared'));
    }],
    ['writing returns to real completed counts', () => {
        const display = renderers.getSyncProgressDisplay({ IsPreparing: false, Prepared: 100, PreparationTotal: 100, Completed: 5, Total: 20 });
        assert.equal(display.percent, 25);
        assert.ok(display.detail.includes('5 / 20'));
        assert.ok(!display.detail.includes('prepared'));
    }],
    ['legacy progress payloads still render', () => {
        const display = renderers.getSyncProgressDisplay({ Completed: 8, Total: 10, Failed: 0 });
        assert.equal(display.percent, 80);
        assert.ok(display.detail.includes('0 failed'));
    }],
    ['per-library progress shows and escapes the preparation phase', () => {
        const element = { innerHTML: '' };
        renderers.renderProgressBar(element, { Phase: 'Preparing movie metadata <test>', IsPreparing: true, Prepared: 1, PreparationTotal: 2, Completed: 0, Total: 20 });
        assert.ok(element.innerHTML.includes('Preparing movie metadata &lt;test&gt;'));
        assert.ok(element.innerHTML.includes('1 / 2 prepared'));
        assert.ok(element.innerHTML.includes('width:50%'));
    }],
    ['failure history exposes the cause without interpreting provider HTML', () => {
        const html = historyHtml({ Success: false, FailureReason: 'VOD category 82 failed: <img src=x onerror=alert(1)> timeout 30 seconds' });
        assert.ok(html.includes('VOD category 82 failed'));
        assert.ok(html.includes('&lt;img'));
        assert.ok(!html.includes('<img'));
        assert.ok(html.includes('Sync-level failure; no individual item failures were recorded.'));
    }],
    ['older failed history clearly identifies unrecorded causes', () => {
        const html = historyHtml({ Success: false });
        assert.ok(html.includes('No failure reason was recorded by this older plugin version'));
    }],
    ['successful history never renders stale failure details', () => {
        const html = historyHtml({ Success: true, FailureReason: 'obsolete failure' });
        assert.ok(!html.includes('obsolete failure'));
        assert.ok(!html.includes('sync-failure-reason'));
    }],
    ['empty and excessive stage counters stay bounded', () => {
        assert.equal(renderers.getSyncProgressDisplay({ IsPreparing: true, Prepared: 1, PreparationTotal: 0 }).percent, 0);
        assert.equal(renderers.getSyncProgressDisplay({ IsPreparing: true, Prepared: 101, PreparationTotal: 100 }).percent, 100);
        assert.equal(renderers.getSyncProgressDisplay({ IsPreparing: true, Prepared: -1, PreparationTotal: 100 }).percent, 0);
    }]
];
for (const [name, run] of tests) {
    run();
    console.log('PASS ' + name);
}
console.log('All ' + tests.length + ' dashboard regression tests passed.');
