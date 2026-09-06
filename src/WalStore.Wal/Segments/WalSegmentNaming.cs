namespace WalStore.Wal.Segments;

/// <summary>
/// Single source of truth for the <c>wal-segment-{N}.log</c> file naming scheme.
/// </summary>
internal static class WalSegmentNaming
{
    internal const string Prefix = "wal-segment-";
    internal const string Extension = ".log";
    internal const string SearchPattern = $"{Prefix}*{Extension}";

    internal static string FileName(int number) => $"{Prefix}{number}{Extension}";

    internal static string FilePath(string directory, int number) =>
        Path.Combine(directory, FileName(number));

    internal static int ParseNumber(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        var numberPart = fileName[Prefix.Length..^Extension.Length];
        return int.Parse(numberPart);
    }

    /// <summary>
    /// Segment files in the directory, ordered by segment number ascending
    /// (oldest first). Returns an empty array when the directory is missing.
    /// </summary>
    internal static string[] GetOrderedSegmentFiles(string directory)
    {
        if (!Directory.Exists(directory))
            return [];

        var files = Directory.GetFiles(directory, SearchPattern);
        Array.Sort(files, (left, right) => ParseNumber(left).CompareTo(ParseNumber(right)));
        return files;
    }
}
