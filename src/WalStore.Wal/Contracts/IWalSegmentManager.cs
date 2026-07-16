namespace WalStore.Wal.Contracts;

public interface IWalSegmentManager : IAsyncDisposable
{
    IWalSegment CurrentSegment { get; }
    Task InitializeAsync(string directory);
    Task RotateAsync(bool forceSync, int maxSegments);
}
