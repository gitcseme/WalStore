using WalStore.Wal.Contracts;

namespace WalStore.Wal.Segments;

public sealed class WalSegmentManager : IWalSegmentManager
{
    private const string SegmentPrefix = "wal-segment-";
    private const string SegmentExtension = ".log";

    private string _directory = string.Empty;
    private IWalSegment? _currentSegment;

    public IWalSegment CurrentSegment =>
        _currentSegment ?? throw new InvalidOperationException("Segment manager not initialized");

    public async Task InitializeAsync(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);

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
            await _currentSegment.DisposeAsync();
    }

    private string[] GetSegmentFiles() =>
        Directory.GetFiles(_directory, $"{SegmentPrefix}*{SegmentExtension}");

    private static int GetLastSegmentFileNumber(string[] files) =>
        files.Max(ParseSegmentNumber);

    private static string GetOldestSegmentFile(string[] files) =>
        files.MinBy(ParseSegmentNumber)!;

    private static int ParseSegmentNumber(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        var numberPart = fileName[SegmentPrefix.Length..^SegmentExtension.Length];
        return int.Parse(numberPart);
    }
}
