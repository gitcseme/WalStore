using System.Text;
using WalStore.Wal;
using WalStore.Wal.Serialization;
using WalStore.Wal.Sync;

namespace WalStore.Wal.Tests;

/// <summary>
/// Recovery as reached through the public <see cref="WriteAheadLog"/> API, which runs it
/// on every open.
/// </summary>
public class WalRecoveryIntegrationTests
{
    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wal_recovery_it_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task NextLsnFollowsTheLastValidRecordAfterTailCorruption()
    {
        var dir = CreateTempDir();
        try
        {
            var config = new WalConfig { Directory = dir, SyncIntervalMs = 50 };

            await using (var wal = await WriteAheadLog.StartAsync(config))
            {
                for (var i = 1; i <= 3; i++)
                    await wal.WriteRecordAsync(Encoding.UTF8.GetBytes($"record{i}"));
            }

            var segmentFile = Directory.GetFiles(dir, "wal-segment-*.log")[0];
            await using (var append = new FileStream(segmentFile, FileMode.Append, FileAccess.Write))
                append.Write([0x08, 0x00, 0x00, 0x00, 0x7F, 0x7F]);

            await using (var wal = await WriteAheadLog.StartAsync(config))
            {
                await wal.WriteRecordAsync("record4"u8.ToArray());

                var records = await wal.ReadAllRecordsAsync();
                Assert.Equal(4, records.Count);
                Assert.Equal(4UL, records[^1].LogSequenceNumber);
                Assert.Equal("record4", Encoding.UTF8.GetString(records[^1].Data.ToByteArray()));
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task EmptiedNewestSegmentKeepsItsNumberAndDoesNotReuseLsns()
    {
        var dir = CreateTempDir();
        try
        {
            // Small segments so the log rotates and older, sealed segments exist.
            var config = new WalConfig
            {
                Directory = dir,
                MaxFileSize = 128,
                MaxSegments = 10,
                SyncIntervalMs = 50
            };

            await using (var wal = await WriteAheadLog.StartAsync(config))
            {
                for (var i = 1; i <= 20; i++)
                    await wal.WriteRecordAsync(Encoding.UTF8.GetBytes($"data-{i:D3}"));
            }

            var segmentFiles = OrderedSegments(dir);
            Assert.True(segmentFiles.Length > 1, "test needs more than one segment");

            var newest = segmentFiles[^1];
            var newestNumber = Path.GetFileName(newest);

            // Records in the wiped segment are legitimately lost, so the log's tail becomes
            // the last record of the segment before it.
            var survivingLastLsn = LastLsnInFile(segmentFiles[^2]);
            Assert.True(survivingLastLsn > 1);

            // Wipe the newest segment entirely: a crash straight after rotation.
            await File.WriteAllBytesAsync(newest, [0x20, 0x00, 0x00, 0x00, .. Enumerable.Repeat((byte)0xAB, 32)]);

            await using (var wal = await WriteAheadLog.StartAsync(config))
            {
                await wal.WriteRecordAsync("after-recovery"u8.ToArray());

                var newLsn = (await wal.ReadAllRecordsAsync())[^1].LogSequenceNumber;

                // Without the fallback to older segments the emptied current segment would
                // report no records, LSNs would restart at 1, and the surviving records in
                // the earlier segments would have duplicates.
                Assert.Equal(survivingLastLsn + 1, newLsn);
                Assert.NotEqual(1UL, newLsn);
            }

            // The emptied segment kept its number, so numbering never went backwards.
            Assert.Contains(newestNumber, Directory.GetFiles(dir, "wal-segment-*.log").Select(Path.GetFileName));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private static string[] OrderedSegments(string dir) =>
        Directory.GetFiles(dir, "wal-segment-*.log")
            .OrderBy(f => int.Parse(Path.GetFileNameWithoutExtension(f)["wal-segment-".Length..]))
            .ToArray();

    private static ulong LastLsnInFile(string path)
    {
        var serializer = new WalRecordSerializer();
        var bytes = File.ReadAllBytes(path);
        var position = 0;
        var lastLsn = 0UL;

        while (position + sizeof(int) <= bytes.Length)
        {
            var size = serializer.ReadSize(bytes.AsSpan(position));
            position += sizeof(int);
            if (size <= 0 || position + size > bytes.Length)
                break;

            lastLsn = serializer.Deserialize(bytes.AsMemory(position, size)).LogSequenceNumber;
            position += size;
        }

        return lastLsn;
    }

    [Fact]
    public void SchedulerRefusesToStartTwice()
    {
        var scheduler = new SyncScheduler(TimeSpan.FromSeconds(1), () => Task.CompletedTask);
        scheduler.Start();

        Assert.Throws<InvalidOperationException>(() => scheduler.Start());
    }
}
