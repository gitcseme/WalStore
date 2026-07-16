namespace WalStore.Wal.Sync;

public sealed class SyncScheduler : IAsyncDisposable
{
    private readonly Func<Task> _syncAction;
    private readonly TimeSpan _interval;
    private CancellationTokenSource? _cts;
    private Task? _task;

    public SyncScheduler(TimeSpan interval, Func<Task> syncAction)
    {
        _interval = interval;
        _syncAction = syncAction;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _task = RunAsync(_cts.Token);
    }

    public async Task StopAsync()
    {
        if (_cts is null)
            return;

        await _cts.CancelAsync();
        if (_task is not null)
        {
            try { await _task; } catch (OperationCanceledException) { }
        }
        _cts.Dispose();
        _cts = null;
        _task = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_interval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
                await _syncAction();
        }
        catch (OperationCanceledException) { }
    }
}
