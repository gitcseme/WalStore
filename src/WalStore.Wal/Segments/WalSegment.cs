using WalStore.Wal.Contracts;

namespace WalStore.Wal.Segments;

public sealed class WalSegment : IWalSegment
{
    private readonly FileStream _stream;
    private readonly int _number;
    private readonly string _filePath;
    private long _bytesWritten;

    public WalSegment(string directory, int number, FileMode mode)
    {
        _number = number;
        _filePath = Path.Combine(directory, $"wal-segment-{number}.log");
        _stream = new FileStream(_filePath, mode, FileAccess.Write,
            FileShare.ReadWrite, 4096, FileOptions.Asynchronous);

        _bytesWritten = mode == FileMode.Create ? 0 : new FileInfo(_filePath).Length;
    }

    public int Number => _number;
    public long BytesWritten => _bytesWritten;
    public string FilePath => _filePath;

    public async Task WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        await _stream.WriteAsync(buffer, ct);
        _bytesWritten += buffer.Length;
    }

    public async Task FlushAsync(CancellationToken ct = default) =>
        await _stream.FlushAsync(ct);

    public void ForceSync() => _stream.Flush(true);

    public async ValueTask DisposeAsync() => await _stream.DisposeAsync();
}
