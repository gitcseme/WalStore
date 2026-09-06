# Write-Ahead Log (WAL) in C#

.NET 10 library implementing a WAL with segmentation, protobuf serialization, CRC32 checksums, periodic background sync, and async concurrency.

## WAL

A Write-Ahead Log (WAL) ensures durability by recording every write operation to an append-only log on stable storage **before** applying it to the main data structures. On a crash, the database replays the log to restore a consistent state. This also enables point-in-time recovery, replication, and atomicity — all without random I/O to the primary store on every write.

## Features

1. **Segmentation** — fixed-size segment files with oldest pruning
2. **CRC32 checksums** — integrity verified on every record
3. **Periodic background sync** — configurable flush interval via `PeriodicTimer`
4. **Force sync** — optional OS-level flush for crash safety
5. **Async concurrency** — thread-safe via `SemaphoreSlim`
6. **LSN persistence** — numbering survives close/reopen
7. **Config persistence** — JSON config file per WAL directory

## Architecture

```
src/WalStore.Wal/
├── Protos/wal_record.proto               # protobuf schema
├── Contracts/                            # 5 interfaces: IWalLogger, IWalSegment,
│                                         #   IWalSegmentManager, IWalRecordSerializer,
│                                         #   IChecksumProvider
├── Segments/WalSegment.cs                # FileStream wrapper
├── Segments/WalSegmentManager.cs         # Discovery, rotation, cleanup
├── Serialization/WalRecordSerializer.cs  # [int32 size][protobuf] format
├── Checksum/Crc32ChecksumProvider.cs     # System.IO.Hashing.Crc32
├── Sync/SyncScheduler.cs                 # PeriodicTimer background sync
└── WriteAheadLog.cs                      # Thin orchestrator
tests/WalStore.Wal.Tests/                 # xUnit tests
```

## Record Format

```protobuf
message WalRecord {
    bytes  data                = 3;
    uint64 log_sequence_number = 1;
    int64  timestamp           = 2;  // ms since Unix epoch
    uint32 checksum            = 4;  // CRC32(data + LSN)
}
```

On disk: `[int32 LE size][protobuf bytes]`. Segments: `wal-segment-{N}.log`.

## Tests

```
dotnet test
```

| Test | What it verifies |
|---|---|
| `WriteAndReadRecords` | Data survives close/reopen cycle |
| `LogSequenceNumberIncrements` | LSN starts at 1, increments by 1 per write |
| `ChecksumIsPresentOnRecords` | Each record has non-zero CRC32 |
| `SegmentRotation` | Max file size triggers rotation; old segments pruned |
| `ReadFromEmptyWalReturnsEmptyList` | Fresh WAL read returns empty |
| `LsnSurvivesRestart` | LSN counter persists across close/reopen |
| `RecoverTruncatesPartialRecord` | Trailing garbage after last valid record is removed |
| `RecoverFixesCorruptedRecord` | Record with wrong CRC32 is discarded, earlier records preserved |
| `RecoverOnHealthyWalIsNoOp` | Recovery on an uncorrupted WAL preserves all records |
