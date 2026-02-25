using System;
using System.Threading;
using System.Threading.Tasks;

public sealed class PollingService : IDisposable
{
    private readonly TimeSpan _interval;
    private CancellationTokenSource _cts;
    private readonly Func<CancellationToken, Task> _work;

    public PollingService(TimeSpan interval, Func<CancellationToken, Task> work)
    {
        _interval = interval;
        _work = work ?? throw new ArgumentNullException(nameof(work));
    }

    public void Start()
    {
        if (_cts != null && !_cts.IsCancellationRequested) return;
        _cts = new CancellationTokenSource();
        _ = RunAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
    }

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await _work(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                // TODO: replace with your logging
                Console.WriteLine($"PollingService error: {ex}");
            }
            sw.Stop();
            var delay = _interval - sw.Elapsed;
            if (delay > TimeSpan.Zero)
            {
                try { await Task.Delay(delay, token).ConfigureAwait(false); }
                catch (TaskCanceledException) { break; }
            }
        }
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); _cts?.Dispose(); } catch { }
    }
}