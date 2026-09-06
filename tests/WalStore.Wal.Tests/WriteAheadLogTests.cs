using System.Text;
using WalStore.Wal;
using WalStore.Wal.Checksum;
using WalStore.Wal.Serialization;

namespace WalStore.Wal.Tests;

public class WriteAheadLogTests
{
    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wal_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task WriteAndReadRecords()
    {
        var dir = CreateTempDir();
        try
        {
            var config = new WalConfig { Directory = dir };
            await using var wal = await WriteAheadLog.StartAsync(config);

            var testData = new[] { "record1", "record2", "record3" };
            foreach (var data in testData)
                await wal.WriteRecordAsync(Encoding.UTF8.GetBytes(data));

            await wal.CloseAsync();

            await using var wal2 = await WriteAheadLog.StartAsync(config);
            var records = await wal2.ReadAllRecordsAsync();

            Assert.Equal(testData.Length, records.Count);
            for (int i = 0; i < testData.Length; i++)
                Assert.Equal(testData[i], Encoding.UTF8.GetString(records[i].Data.ToByteArray()));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task LogSequenceNumberIncrements()
    {
        var dir = CreateTempDir();
        try
        {
            var config = new WalConfig { Directory = dir };
            await using var wal = await WriteAheadLog.StartAsync(config);

            for (int i = 1; i <= 5; i++)
                await wal.WriteRecordAsync(Encoding.UTF8.GetBytes($"data{i}"));

            await wal.CloseAsync();

            await using var wal2 = await WriteAheadLog.StartAsync(config);
            var records = await wal2.ReadAllRecordsAsync();

            Assert.Equal(5, records.Count);
            Assert.Equal(5UL, records[^1].LogSequenceNumber);
            for (int i = 0; i < 5; i++)
                Assert.Equal((ulong)(i + 1), records[i].LogSequenceNumber);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ChecksumIsPresentOnRecords()
    {
        var dir = CreateTempDir();
        try
        {
            var config = new WalConfig { Directory = dir };
            await using var wal = await WriteAheadLog.StartAsync(config);

            await wal.WriteRecordAsync("hello"u8.ToArray());
            await wal.CloseAsync();

            await using var wal2 = await WriteAheadLog.StartAsync(config);
            var records = await wal2.ReadAllRecordsAsync();

            Assert.Single(records);
            Assert.NotEqual(0u, records[0].Checksum);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task SegmentRotation()
    {
        var dir = CreateTempDir();
        try
        {
            var config = new WalConfig
            {
                Directory = dir,
                MaxFileSize = 512, // small threshold to force rotation
                MaxSegments = 3,
                SyncIntervalMs = 50
            };

            await using var wal = await WriteAheadLog.StartAsync(config);

            for (int i = 0; i < 100; i++)
                await wal.WriteRecordAsync(Encoding.UTF8.GetBytes($"data-{i:D4}"));

            await wal.CloseAsync();

            var files = Directory.GetFiles(dir, "wal-segment-*.log");
            foreach (var file in files)
            {
                var info = new FileInfo(file);
                Assert.True(info.Length <= config.MaxFileSize,
                    $"Segment {file} size {info.Length} exceeds max {config.MaxFileSize}");
            }

            Assert.Equal(config.MaxSegments, files.Length);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ReadFromEmptyWalReturnsEmptyList()
    {
        var dir = CreateTempDir();
        try
        {
            var config = new WalConfig { Directory = dir };
            await using var wal = await WriteAheadLog.StartAsync(config);
            var records = await wal.ReadAllRecordsAsync();

            Assert.Empty(records);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task RecoverTruncatesPartialRecord()
    {
        var dir = CreateTempDir();
        try
        {
            var config = new WalConfig { Directory = dir, SyncIntervalMs = 50 };
            await using (var wal = await WriteAheadLog.StartAsync(config))
            {
                await wal.WriteRecordAsync("record1"u8.ToArray());
                await wal.WriteRecordAsync("record2"u8.ToArray());
                await wal.WriteRecordAsync("record3"u8.ToArray());
            }

            var segmentFile = Directory.GetFiles(dir, "wal-segment-*.log")[0];
            await using (var append = new FileStream(segmentFile, FileMode.Append, FileAccess.Write))
            {
                append.WriteByte(0xFF);
                append.WriteByte(0xFF);
                append.WriteByte(0xFF);
                append.WriteByte(0xFF);
                append.WriteByte(0xFF);
            }

            await using (var wal = await WriteAheadLog.StartAsync(config))
            {
                var records = await wal.ReadAllRecordsAsync();
                Assert.Equal(3, records.Count);
                Assert.Equal("record1", Encoding.UTF8.GetString(records[0].Data.ToByteArray()));
                Assert.Equal("record2", Encoding.UTF8.GetString(records[1].Data.ToByteArray()));
                Assert.Equal("record3", Encoding.UTF8.GetString(records[2].Data.ToByteArray()));
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task RecoverFixesCorruptedRecord()
    {
        var dir = CreateTempDir();
        try
        {
            var config = new WalConfig { Directory = dir, SyncIntervalMs = 50 };
            await using (var wal = await WriteAheadLog.StartAsync(config))
            {
                await wal.WriteRecordAsync("record1"u8.ToArray());
                await wal.WriteRecordAsync("record2"u8.ToArray());
                await wal.WriteRecordAsync("record3"u8.ToArray());
            }

            var segmentFile = Directory.GetFiles(dir, "wal-segment-*.log")[0];
            var bytes = await File.ReadAllBytesAsync(segmentFile);
            var offset = 0;
            var size1 = BitConverter.ToInt32(bytes, offset);
            offset += 4 + size1;
            var size2 = BitConverter.ToInt32(bytes, offset);
            var rng = new Random(42);
            rng.NextBytes(bytes.AsSpan(offset + 4, size2));
            await File.WriteAllBytesAsync(segmentFile, bytes);

            await using (var wal = await WriteAheadLog.StartAsync(config))
            {
                var records = await wal.ReadAllRecordsAsync();
                Assert.Single(records);
                Assert.Equal("record1", Encoding.UTF8.GetString(records[0].Data.ToByteArray()));
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task RecoverOnHealthyWalIsNoOp()
    {
        var dir = CreateTempDir();
        try
        {
            var config = new WalConfig { Directory = dir, SyncIntervalMs = 50 };
            await using (var wal = await WriteAheadLog.StartAsync(config))
            {
                for (int i = 1; i <= 5; i++)
                    await wal.WriteRecordAsync(Encoding.UTF8.GetBytes($"data{i}"));
            }

            var segmentFile = Directory.GetFiles(dir, "wal-segment-*.log")[0];

            await using (var wal = await WriteAheadLog.StartAsync(config))
            {
                var records = await wal.ReadAllRecordsAsync();
                Assert.Equal(5, records.Count);
                for (int i = 0; i < 5; i++)
                {
                    Assert.Equal($"data{i + 1}", Encoding.UTF8.GetString(records[i].Data.ToByteArray()));
                    Assert.Equal((ulong)(i + 1), records[i].LogSequenceNumber);
                }
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task LsnSurvivesRestart()
    {
        var dir = CreateTempDir();
        try
        {
            var config = new WalConfig { Directory = dir };

            await using (var wal = await WriteAheadLog.StartAsync(config))
            {
                await wal.WriteRecordAsync("a"u8.ToArray());
                await wal.WriteRecordAsync("b"u8.ToArray());
                await wal.CloseAsync();
            }

            await using (var wal = await WriteAheadLog.StartAsync(config))
            {
                await wal.WriteRecordAsync("c"u8.ToArray());
                await wal.CloseAsync();
            }

            await using (var wal = await WriteAheadLog.StartAsync(config))
            {
                var records = await wal.ReadAllRecordsAsync();
                Assert.Equal(3, records.Count);
                Assert.Equal(3UL, records[^1].LogSequenceNumber);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
