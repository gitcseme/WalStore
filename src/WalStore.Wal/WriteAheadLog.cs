using Google.Protobuf;
using WalStore.Wal.Checksum;
using WalStore.Wal.Contracts;
using WalStore.Wal.Segments;
using WalStore.Wal.Serialization;
using WalStore.Wal.Sync;

namespace WalStore.Wal;

public sealed class WriteAheadLog : IWalLogger
{
    private readonly IWalSegmentManager _segmentManager;
    private readonly IWalRecordSerializer _serializer;
    private readonly IChecksumProvider _checksumProvider;
    private readonly SyncScheduler _scheduler;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly WalConfig _config;
    private ulong _lastLsn;
    private bool _disposed;

    private WriteAheadLog(WalConfig config, IWalSegmentManager segmentManager,
        IWalRecordSerializer serializer, IChecksumProvider checksumProvider)
    {
        _config = config;
        _segmentManager = segmentManager;
        _serializer = serializer;
        _checksumProvider = checksumProvider;
        var interval = config.SyncIntervalMs > 0
            ? TimeSpan.FromMilliseconds(config.SyncIntervalMs)
            : TimeSpan.FromMilliseconds(200);
        _scheduler = new SyncScheduler(interval, FlushAsync);
    }

    public static async Task<IWalLogger> StartAsync(WalConfig config)
    {
        var segmentManager = new WalSegmentManager();
        await segmentManager.InitializeAsync(config.Directory);

        var wal = new WriteAheadLog(config, segmentManager,
            new WalRecordSerializer(), new Crc32ChecksumProvider());

        wal._lastLsn = await wal.DiscoverLastLsnAsync();
        wal._scheduler.Start();
        return wal;
    }

    public async Task WriteRecordAsync(byte[] data, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _lock.WaitAsync(ct);
        try
        {
            var lsn = _lastLsn + 1;
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var checksum = _checksumProvider.Compute(data, lsn);

            var record = new WalRecord
            {
                Data = ByteString.CopyFrom(data),
                LogSequenceNumber = lsn,
                Timestamp = timestamp,
                Checksum = checksum
            };

            var serialized = _serializer.SerializeWithSizePrefix(record);
            var segment = _segmentManager.CurrentSegment;

            if (segment.BytesWritten + serialized.Length >= _config.MaxFileSize)
            {
                await _segmentManager.RotateAsync(_config.EnableForceSync, _config.MaxSegments);
            }

            await _segmentManager.CurrentSegment.WriteAsync(serialized, ct);
            _lastLsn = lsn;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<List<WalRecord>> ReadAllRecordsAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _lock.WaitAsync(ct);
        try
        {
            await _segmentManager.CurrentSegment.FlushAsync(ct);

            var records = new List<WalRecord>();
            var filePath = _segmentManager.CurrentSegment.FilePath;
            var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite, 4096, FileOptions.Asynchronous);

            await using (fileStream)
            {
                var sizeBuf = new byte[sizeof(int)];
                while (true)
                {
                    // Last read returns 0 bytes, indicating the EOF.
                    var bytesRead = await fileStream.ReadAsync(sizeBuf, ct);
                    if (bytesRead < sizeof(int))
                        break;

                    var recordSize = _serializer.ReadSize(sizeBuf);
                    var retrievedRecordData = new byte[recordSize];
                    var totalRead = 0;

                    while (totalRead < recordSize)
                    {
                        // ReadAsync may return fewer bytes than requested,
                        // so keep reading until the full record is read.
                        var read = await fileStream.ReadAsync(
                            retrievedRecordData.AsMemory(totalRead, recordSize - totalRead), ct);

                        if (read == 0) break;

                        totalRead += read;
                    }

                    if (totalRead < recordSize) break;

                    records.Add(_serializer.Deserialize(retrievedRecordData));
                }
            }

            return records;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task CloseAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await _scheduler.StopAsync();

        await _lock.WaitAsync();
        try
        {
            var segment = _segmentManager.CurrentSegment;
            await segment.FlushAsync();
            if (_config.EnableForceSync)
                segment.ForceSync();
        }
        finally
        {
            _lock.Release();
        }

        await _segmentManager.DisposeAsync();
        await _scheduler.DisposeAsync();
    }

    public async ValueTask DisposeAsync() => await CloseAsync();

    private async Task FlushAsync()
    {
        if (_disposed) return;

        await _lock.WaitAsync();
        try
        {
            if (!_disposed)
                await _segmentManager.CurrentSegment.FlushAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<ulong> DiscoverLastLsnAsync()
    {
        var records = await ReadAllRecordsAsync(CancellationToken.None);
        return records.Count == 0 ? 0UL : records[^1].LogSequenceNumber;
    }
}
