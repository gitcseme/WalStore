using System.Text;
using WalStore.Wal;

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
