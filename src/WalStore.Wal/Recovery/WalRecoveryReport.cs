namespace WalStore.Wal.Recovery;

/// <summary>What recovery did to a single segment file.</summary>
public enum WalSegmentRecoveryOutcome
{
    /// <summary>Every record was valid; the file was not rewritten.</summary>
    AllLogValid = 1,

    /// <summary>The file held a valid prefix; bytes after it were discarded.</summary>
    Truncated = 2,

    /// <summary>
    /// No valid prefix survived, so the file was emptied. The file itself is kept
    /// so that segment numbering — and therefore LSN continuity — is preserved.
    /// </summary>
    Emptied = 3
}

public readonly record struct WalSegmentRecoveryResult(
    string FilePath,
    WalSegmentRecoveryOutcome Outcome,
    long ValidBytes,
    long OriginalBytes);

public sealed record WalRecoveryReport(IReadOnlyList<WalSegmentRecoveryResult> Segments)
{
    public static WalRecoveryReport Empty { get; } = new([]);

    public bool AnyRepaired =>
        Segments.Any(segment => segment.Outcome != WalSegmentRecoveryOutcome.AllLogValid);
}
