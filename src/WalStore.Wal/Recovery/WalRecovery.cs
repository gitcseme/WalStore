using WalStore.Wal.Contracts;
using WalStore.Wal.Segments;

namespace WalStore.Wal.Recovery;

/// <summary>
/// Crash recovery for a WAL directory.
///
/// A crash can only tear the tail of the log: a partial <c>write</c>, or a power loss
/// with pages still in the OS cache, leaves an incomplete or corrupt record at the end.
/// Recovery therefore scans forward from the oldest segment, validating framing and
/// checksums, and stops at the first record that does not verify. That offset is the end
/// of the log — everything after it is discarded, so the log stays a *prefix* of the
/// records that were written. A bad record is never skipped over to salvage later ones;
/// that would leave a hole in the LSN sequence.
///
/// Only fsynced records are recoverable. Records that reached the OS but not the disk are
/// legitimately lost — they were never acknowledged.
///
/// This type touches nothing but the file system, the serializer and the checksum
/// provider. It must run while no <see cref="WalSegment"/> handle is open: segments are
/// opened <c>FileShare.ReadWrite</c> without <c>FileShare.Delete</c>, an appending handle
/// would write *past* a torn tail, and <see cref="WalSegment"/> caches the file length at
/// construction time.
/// </summary>
public sealed class WalRecovery : IWalRecovery
{
    private readonly IWalRecordSerializer _serializer;
    private readonly IChecksumProvider _checksumProvider;

    public WalRecovery(IWalRecordSerializer serializer, IChecksumProvider checksumProvider)
    {
        _serializer = serializer;
        _checksumProvider = checksumProvider;
    }

    public async Task<WalRecoveryReport> RecoverDirectoryAsync(
        string directory, CancellationToken ct = default)
    {
        var segmentFiles = WalSegmentNaming.GetOrderedSegmentFiles(directory);
        if (segmentFiles.Length == 0)
            return WalRecoveryReport.Empty;

        var results = new List<WalSegmentRecoveryResult>(segmentFiles.Length);
        var logEnded = false;

        foreach (var segmentFile in segmentFiles)
        {
            if (logEnded)
            {
                // The log already ended in an earlier segment. Anything here sits after a
                // hole and cannot be replayed, so it goes.
                results.Add(await EmptySegmentAsync(segmentFile, ct));
                continue;
            }

            var result = await RecoverSegmentFileAsync(segmentFile, ct);
            results.Add(result);

            if (result.Outcome != WalSegmentRecoveryOutcome.AllLogValid)
                logEnded = true;
        }

        return new WalRecoveryReport(results);
    }

    private async Task<WalSegmentRecoveryResult> RecoverSegmentFileAsync(
        string filePath, CancellationToken ct)
    {
        var fileBytes = await File.ReadAllBytesAsync(filePath, ct);

        // Case 1: Already empty
        if (fileBytes.Length == 0)
            return new WalSegmentRecoveryResult(filePath, WalSegmentRecoveryOutcome.AllLogValid, 0, 0);

        var endOfLastValid = ScanValidPrefix(fileBytes);

        // Case 2: All logs in the file are valid
        if (endOfLastValid == fileBytes.Length)
            return new WalSegmentRecoveryResult(
                filePath, WalSegmentRecoveryOutcome.AllLogValid, endOfLastValid, fileBytes.Length);

        // Case 3: None of the logs are valid in the file
        if (endOfLastValid == 0)
        {
            await File.WriteAllBytesAsync(filePath, [], ct);
            return new WalSegmentRecoveryResult(
                filePath, WalSegmentRecoveryOutcome.Emptied, 0, fileBytes.Length);
        }

        // Case 4: Partialy valid from begining
        await File.WriteAllBytesAsync(filePath, fileBytes[..endOfLastValid], ct);
        return new WalSegmentRecoveryResult(
            filePath, WalSegmentRecoveryOutcome.Truncated, endOfLastValid, fileBytes.Length);
    }

    /// <summary>
    /// Returns the offset just past the last record that is completely present, parses,
    /// and whose checksum matches — i.e. the length of the valid prefix.
    /// </summary>
    private int ScanValidPrefix(byte[] fileBytes)
    {
        var position = 0;
        var endOfLastValid = 0;

        while (position + sizeof(int) <= fileBytes.Length)
        {
            var size = _serializer.ReadSize(fileBytes.AsSpan(position));
            var payloadStart = position + sizeof(int);

            // A non-positive or oversized length means the size prefix itself is torn or
            // garbage, so there is nothing further to trust.
            if (size <= 0 || (long)payloadStart + size > fileBytes.Length)
                break;

            try
            {
                var record = _serializer.Deserialize(fileBytes.AsMemory(payloadStart, size));
                var expected = _checksumProvider.Compute(record.Data.Span, record.LogSequenceNumber);
                if (record.Checksum != expected)
                    break;
            }
            catch
            {
                // Undecodable protobuf is corruption like any other: the log ends here.
                break;
            }

            position = payloadStart + size;
            endOfLastValid = position;
        }

        return endOfLastValid;
    }

    private static async Task<WalSegmentRecoveryResult> EmptySegmentAsync(
        string filePath, CancellationToken ct)
    {
        var originalBytes = new FileInfo(filePath).Length;
        if (originalBytes > 0)
            await File.WriteAllBytesAsync(filePath, [], ct);

        return new WalSegmentRecoveryResult(
            filePath, WalSegmentRecoveryOutcome.Emptied, 0, originalBytes);
    }
}
