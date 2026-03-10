using System;
using System.Threading;
using System.Threading.Tasks;

public class PollingService : IDisposable
{
    private readonly TimeSpan _baseInterval = TimeSpan.FromSeconds(1);
    private CancellationTokenSource _cts;
    private Task _loopTask;

    public event EventHandler<PollResultEventArgs> PollResult;

    public void Start()
    {
        if (_loopTask != null && !_loopTask.IsCompleted) return;
        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => PollLoopAsync(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _loopTask?.Wait(); } catch { }
        _cts?.Dispose();
        _cts = null;
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        var interval = _baseInterval;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await DoPollAsync(ct).ConfigureAwait(false);

                // raise event on threadpool; UI must Invoke when handling
                PollResult?.Invoke(this, new PollResultEventArgs(result));

                // reset interval on success
                interval = _baseInterval;
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                // log ex
                // exponential backoff to avoid tight loop on repeated failures
                interval = TimeSpan.FromMilliseconds(Math.Min(interval.TotalMilliseconds * 2, 10000));
            }

            try
            {
                await Task.Delay(interval, ct).ConfigureAwait(false);
            }
            catch (TaskCanceledException) { break; }
        }
    }

    private async Task<object> DoPollAsync(CancellationToken ct)
    {
        // TODO: replace with actual async I/O (DB/device)
        await Task.Yield();
        return new { Timestamp = DateTime.UtcNow };
    }

    public void Dispose() => Stop();
}

public class PollResultEventArgs : EventArgs
{
    public object Data { get; }
    public PollResultEventArgs(object data) => Data = data;
}