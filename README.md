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
8. **Crash recovery** — checksum-verified prefix restored on open

## Architecture

```
src/WalStore.Wal/
├── Protos/wal_record.proto               # protobuf schema
├── Contracts/                            # interfaces: IWalLogger, IWalSegment,
│                                         #   IWalSegmentManager, IWalRecordSerializer,
│                                         #   IChecksumProvider, IWalRecovery
├── Segments/WalSegment.cs                # FileStream wrapper
├── Segments/WalSegmentManager.cs         # Recovery-on-open, rotation, cleanup
├── Segments/WalSegmentNaming.cs          # wal-segment-{N}.log naming
├── Serialization/WalRecordSerializer.cs  # [int32 size][protobuf] format
├── Checksum/Crc32ChecksumProvider.cs     # System.IO.Hashing.Crc32
├── Recovery/WalRecovery.cs               # Crash recovery (truncate to valid prefix)
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

## Recovery

A crash can only tear the tail of the log — a partial `write`, or a power loss with pages
still in the OS cache. Recovery scans forward from the oldest segment, validating framing
and CRC32, and stops at the first record that does not verify: that offset is the end of
the log. The valid prefix is kept, everything after it is discarded, and later segments are
emptied, so the log always stays a *prefix* of the records that were written. A corrupt
record is never skipped to salvage records behind it — that would leave a hole in the LSN
sequence.

It runs inside `WalSegmentManager.InitializeAsync`, before a segment file is opened, so
every `WriteAheadLog.StartAsync` recovers. It returns a `WalRecoveryReport` describing each segment's outcome
(`AllLogValid`, `Truncated`, `Emptied`). Repair needs exclusive access to the files, so to
re-validate an existing log, close it and call `WalRecovery.RecoverDirectoryAsync` yourself.

A segment with no valid prefix is emptied rather than deleted, so segment numbering — and
therefore LSN continuity — survives the repair. Only fsynced records are recoverable;
records that reached the OS but not the disk were never acknowledged.

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

`WalRecoveryTests` covers one corruption scenario per test against `WalRecovery` directly:

| Test | Scenario |
|---|---|
| `TruncatesRecordWithIncompletePayload` | Crash mid-append — size prefix written, payload short |
| `TruncatesPartialSizePrefix` | Fewer than 4 trailing bytes |
| `TruncatesAtRecordWithBadChecksum` | CRC mismatch; later valid records discarded too |
| `TruncatesZeroSizePrefix` / `TruncatesOversizedSizePrefixWithoutThrowing` | Garbage size prefix |
| `EmptiesSegmentWithNoValidPrefixButKeepsTheFile` | Nothing valid — file emptied, number kept |
| `DiscardsEverySegmentAfterTheOneThatEndsTheLog` | Corruption in an earlier segment ends the log |
| `HealthyLogIsNotRewritten` | No-op on a clean log — bytes and mtime untouched |
| `MissingDirectoryIsNoOp` / `DirectoryWithNoSegmentsIsNoOp` | Nothing to recover |

`WalRecoveryIntegrationTests` drives the same paths through `StartAsync`: that a repaired
log continues its LSN sequence rather than reusing numbers, and that an emptied segment
keeps its number.
