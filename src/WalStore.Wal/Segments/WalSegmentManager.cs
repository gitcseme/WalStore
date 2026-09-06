using WalStore.Wal.Checksum;
using WalStore.Wal.Contracts;
using WalStore.Wal.Recovery;
using WalStore.Wal.Serialization;

namespace WalStore.Wal.Segments;

public sealed class WalSegmentManager : IWalSegmentManager
{
    private readonly IWalRecovery _recovery;

    private string _directory = string.Empty;
    private IWalSegment? _currentSegment;

    public WalSegmentManager()
        : this(new WalRecovery(new WalRecordSerializer(), new Crc32ChecksumProvider()))
    {
    }

    public WalSegmentManager(IWalRecovery recovery) => _recovery = recovery;

    public IWalSegment CurrentSegment =>
        _currentSegment ?? throw new InvalidOperationException("Segment manager not initialized");

    public async Task InitializeAsync(string directory, CancellationToken ct = default)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);

        // Repair before opening anything: recovery rewrites segment files, and segments are
        // opened without FileShare.Delete, appending, with their length cached at open.
        await _recovery.RecoverDirectoryAsync(directory, ct);

        var files = GetSegmentFiles();

        if (files.Length == 0)
        {
            _currentSegment = new WalSegment(directory, 1, FileMode.Create);
            return;
        }

        var lastNumber = GetLastSegmentFileNumber(files);
        _currentSegment = new WalSegment(directory, lastNumber, FileMode.Append);
    }

    public async Task RotateAsync(bool forceSync, int maxSegments)
    {
        if (_currentSegment is not null)
        {
            await _currentSegment.FlushAsync();
            if (forceSync) _currentSegment.ForceSync();
            await _currentSegment.DisposeAsync();
        }

        var newNumber = (_currentSegment?.Number ?? 0) + 1;

        if (newNumber > maxSegments)
        {
            var files = GetSegmentFiles();
            if (files.Length > 0)
                File.Delete(GetOldestSegmentFile(files));
        }

        _currentSegment = new WalSegment(_directory, newNumber, FileMode.Create);
    }

    public async ValueTask DisposeAsync()
    {
        if (_currentSegment is not null)
        {
            await _currentSegment.DisposeAsync();
            _currentSegment = null;
        }
    }

    private string[] GetSegmentFiles() =>
        Directory.GetFiles(_directory, WalSegmentNaming.SearchPattern);

    private static int GetLastSegmentFileNumber(string[] files) =>
        files.Max(WalSegmentNaming.ParseNumber);

    private static string GetOldestSegmentFile(string[] files) =>
        files.MinBy(WalSegmentNaming.ParseNumber)!;
}
