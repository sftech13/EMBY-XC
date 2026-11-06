using System;
using System.Threading;
using System.Threading.Tasks;

namespace Emby.Xtream.Plugin.Service
{
    // One queue per related library pair. Acquire before reading configuration or
    // publishing progress, so a waiting run cannot overwrite the active run's state.
    internal sealed class SyncRunQueue
    {
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        internal async Task RunAsync(
            Func<Task> run,
            CancellationToken cancellationToken,
            Action onWaiting = null,
            Action onResuming = null)
        {
            if (run == null) throw new ArgumentNullException(nameof(run));

            var waited = !await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false);
            if (waited)
            {
                onWaiting?.Invoke();
                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (waited) onResuming?.Invoke();
                await run().ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}
