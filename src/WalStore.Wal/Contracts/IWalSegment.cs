namespace WalStore.Wal.Contracts;

public interface IWalSegment : IAsyncDisposable
{
    int Number { get; }
    long BytesWritten { get; }
    string FilePath { get; }
    Task WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default);
    Task FlushAsync(CancellationToken ct = default);
    void ForceSync();
}
