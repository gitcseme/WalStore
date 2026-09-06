namespace WalStore.Wal.Contracts;

public interface IWalLogger : IAsyncDisposable
{
    Task WriteRecordAsync(byte[] data, CancellationToken ct = default);
    Task<List<WalRecord>> ReadAllRecordsAsync(CancellationToken ct = default);
    Task CloseAsync();
}
