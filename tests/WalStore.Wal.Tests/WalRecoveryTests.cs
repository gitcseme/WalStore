using System.Text;
using Google.Protobuf;
using WalStore.Wal;
using WalStore.Wal.Checksum;
using WalStore.Wal.Recovery;
using WalStore.Wal.Serialization;

namespace WalStore.Wal.Tests;

/// <summary>
/// Recovery scenarios, driven against <see cref="WalRecovery"/> with hand-built segment
/// files so each form of tail damage can be produced exactly.
/// </summary>
public class WalRecoveryTests
{
    private static readonly WalRecordSerializer Serializer = new();
    private static readonly Crc32ChecksumProvider Checksums = new();

    private static WalRecovery CreateRecovery() => new(Serializer, Checksums);

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wal_recovery_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>A framed, checksum-correct record as it would appear on disk.</summary>
    private static byte[] Record(ulong lsn, string data, uint? overrideChecksum = null)
    {
        var payload = Encoding.UTF8.GetBytes(data);
        var record = new WalRecord
        {
            Data = ByteString.CopyFrom(payload),
            LogSequenceNumber = lsn,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Checksum = overrideChecksum ?? Checksums.Compute(payload, lsn)
        };
        return Serializer.SerializeWithSizePrefix(record);
    }

    private static string WriteSegment(string dir, int number, params byte[][] chunks)
    {
        var path = Path.Combine(dir, $"wal-segment-{number}.log");
        File.WriteAllBytes(path, chunks.SelectMany(c => c).ToArray());
        return path;
    }

    private static WalSegmentRecoveryResult Single(WalRecoveryReport report)
    {
        Assert.Single(report.Segments);
        return report.Segments[0];
    }

    // --- S1: crash mid-append, payload short ------------------------------------------

    [Fact]
    public async Task TruncatesRecordWithIncompletePayload()
    {
        var dir = CreateTempDir();
        try
        {
            var r1 = Record(1, "record1");
            var r2 = Record(2, "record2");
            var torn = Record(3, "record3")[..5]; // size prefix plus one payload byte
            var path = WriteSegment(dir, 1, r1, r2, torn);

            var result = Single(await CreateRecovery().RecoverDirectoryAsync(dir));

            Assert.Equal(WalSegmentRecoveryOutcome.Truncated, result.Outcome);
            Assert.Equal(r1.Length + r2.Length, result.ValidBytes);
            Assert.Equal(r1.Length + r2.Length, new FileInfo(path).Length);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // --- S2: partial size prefix -------------------------------------------------------

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task TruncatesPartialSizePrefix(int trailingBytes)
    {
        var dir = CreateTempDir();
        try
        {
            var r1 = Record(1, "record1");
            var path = WriteSegment(dir, 1, r1, Enumerable.Repeat((byte)0xFF, trailingBytes).ToArray());

            var result = Single(await CreateRecovery().RecoverDirectoryAsync(dir));

            Assert.Equal(WalSegmentRecoveryOutcome.Truncated, result.Outcome);
            Assert.Equal(r1.Length, result.ValidBytes);
            Assert.Equal(r1.Length, new FileInfo(path).Length);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // --- S3: checksum mismatch ---------------------------------------------------------

    [Fact]
    public async Task TruncatesAtRecordWithBadChecksum()
    {
        var dir = CreateTempDir();
        try
        {
            var r1 = Record(1, "record1");
            var bad = Record(2, "record2", overrideChecksum: 0xDEADBEEF);
            var r3 = Record(3, "record3");
            var path = WriteSegment(dir, 1, r1, bad, r3);

            var result = Single(await CreateRecovery().RecoverDirectoryAsync(dir));

            // The log ends at the bad record: record3 is discarded even though it is valid,
            // because keeping it would leave a hole in the LSN sequence.
            Assert.Equal(WalSegmentRecoveryOutcome.Truncated, result.Outcome);
            Assert.Equal(r1.Length, result.ValidBytes);
            Assert.Equal(r1.Length, new FileInfo(path).Length);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // --- S4: garbage size prefix -------------------------------------------------------

    [Fact]
    public async Task TruncatesZeroSizePrefix()
    {
        var dir = CreateTempDir();
        try
        {
            var r1 = Record(1, "record1");
            var path = WriteSegment(dir, 1, r1, BitConverter.GetBytes(0));

            var result = Single(await CreateRecovery().RecoverDirectoryAsync(dir));

            Assert.Equal(WalSegmentRecoveryOutcome.Truncated, result.Outcome);
            Assert.Equal(r1.Length, new FileInfo(path).Length);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task TruncatesOversizedSizePrefixWithoutThrowing()
    {
        var dir = CreateTempDir();
        try
        {
            var r1 = Record(1, "record1");
            var path = WriteSegment(dir, 1, r1, BitConverter.GetBytes(int.MaxValue));

            var result = Single(await CreateRecovery().RecoverDirectoryAsync(dir));

            Assert.Equal(WalSegmentRecoveryOutcome.Truncated, result.Outcome);
            Assert.Equal(r1.Length, new FileInfo(path).Length);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // --- S5: nothing valid in the segment ---------------------------------------------

    [Fact]
    public async Task EmptiesSegmentWithNoValidPrefixButKeepsTheFile()
    {
        var dir = CreateTempDir();
        try
        {
            var bad = Record(1, "record1", overrideChecksum: 0xDEADBEEF);
            var path = WriteSegment(dir, 1, bad);

            var result = Single(await CreateRecovery().RecoverDirectoryAsync(dir));

            Assert.Equal(WalSegmentRecoveryOutcome.Emptied, result.Outcome);
            Assert.Equal(0, result.ValidBytes);
            Assert.Equal(bad.Length, result.OriginalBytes);

            // Keeping the file preserves segment numbering, which is what stops LSNs from
            // being handed out twice after a repair.
            Assert.True(File.Exists(path));
            Assert.Equal(0, new FileInfo(path).Length);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // --- S6: corruption in an earlier segment ends the whole log ----------------------

    [Fact]
    public async Task DiscardsEverySegmentAfterTheOneThatEndsTheLog()
    {
        var dir = CreateTempDir();
        try
        {
            var s1 = WriteSegment(dir, 1, Record(1, "a"), Record(2, "b"));
            var keptInSegment2 = Record(3, "c");
            var s2 = WriteSegment(dir, 2, keptInSegment2, Record(4, "d", overrideChecksum: 1));
            var s3 = WriteSegment(dir, 3, Record(5, "e"), Record(6, "f"));

            var report = await CreateRecovery().RecoverDirectoryAsync(dir);

            Assert.Equal(3, report.Segments.Count);
            Assert.True(report.AnyRepaired);

            Assert.Equal(WalSegmentRecoveryOutcome.AllLogValid, report.Segments[0].Outcome);
            Assert.Equal(WalSegmentRecoveryOutcome.Truncated, report.Segments[1].Outcome);
            Assert.Equal(WalSegmentRecoveryOutcome.Emptied, report.Segments[2].Outcome);

            Assert.Equal(keptInSegment2.Length, new FileInfo(s2).Length);
            Assert.Equal(0, new FileInfo(s3).Length);
            Assert.True(new FileInfo(s1).Length > 0);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // --- S7: healthy log --------------------------------------------------------------

    [Fact]
    public async Task HealthyLogIsNotRewritten()
    {
        var dir = CreateTempDir();
        try
        {
            var s1 = WriteSegment(dir, 1, Record(1, "a"), Record(2, "b"));
            var s2 = WriteSegment(dir, 2, Record(3, "c"));

            var before = new[] { s1, s2 }
                .ToDictionary(p => p, p => (Bytes: File.ReadAllBytes(p), Written: new FileInfo(p).LastWriteTimeUtc));

            var report = await CreateRecovery().RecoverDirectoryAsync(dir);

            Assert.Equal(2, report.Segments.Count);
            Assert.All(report.Segments, s => Assert.Equal(WalSegmentRecoveryOutcome.AllLogValid, s.Outcome));
            Assert.False(report.AnyRepaired);

            foreach (var (path, snapshot) in before)
            {
                Assert.Equal(snapshot.Bytes, File.ReadAllBytes(path));
                Assert.Equal(snapshot.Written, new FileInfo(path).LastWriteTimeUtc);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task EmptySegmentFileIsIntact()
    {
        var dir = CreateTempDir();
        try
        {
            WriteSegment(dir, 1);

            var result = Single(await CreateRecovery().RecoverDirectoryAsync(dir));

            Assert.Equal(WalSegmentRecoveryOutcome.AllLogValid, result.Outcome);
            Assert.False(result.Outcome != WalSegmentRecoveryOutcome.AllLogValid);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // --- S8: nothing to recover -------------------------------------------------------

    [Fact]
    public async Task MissingDirectoryIsNoOp()
    {
        var missing = Path.Combine(Path.GetTempPath(), "wal_recovery_missing_" + Guid.NewGuid().ToString("N"));

        var report = await CreateRecovery().RecoverDirectoryAsync(missing);

        Assert.Empty(report.Segments);
        Assert.False(report.AnyRepaired);
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public async Task DirectoryWithNoSegmentsIsNoOp()
    {
        var dir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "wal.config.json"), "{}");

            var report = await CreateRecovery().RecoverDirectoryAsync(dir);

            Assert.Empty(report.Segments);
            Assert.False(report.AnyRepaired);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
