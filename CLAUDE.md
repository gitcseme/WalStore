# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

.NET 10 write-ahead log library (`src/WalStore.Wal`) with an xUnit test project. Solution file is `WalStore.slnx` (the new XML solution format — requires a recent `dotnet`/VS 18).

## Commands

```powershell
dotnet build

dotnet test
dotnet test --filter "FullyQualifiedName~SegmentRotation"   # single test
```

Protobuf C# is generated at build time by `Grpc.Tools` from `src/WalStore.Wal/Protos/*.proto` into `src/WalStore.Wal/Generated/Protos/` (checked in). After editing the `.proto`, rebuild rather than hand-editing `Generated/`.

## Architecture

`WriteAheadLog` (`src/WalStore.Wal/WriteAheadLog.cs`) is an orchestrator only — every mechanism is behind one of the interfaces in `Contracts/` and injected through the `internal` constructor:

- `IWalSegmentManager` (`Segments/WalSegmentManager.cs`) — owns the segment directory: recovers it on `InitializeAsync`, discovers `wal-segment-{N}.log` files, owns `CurrentSegment`, rotates and prunes the oldest segment once `N > MaxSegments`.
- `IWalSegment` (`Segments/WalSegment.cs`) — a write-only `FileStream` plus a `BytesWritten` counter (seeded from `FileInfo.Length` when opened `FileMode.Append`). `ForceSync()` is `Flush(true)`.
- `IWalRecordSerializer` — on-disk framing is `[int32 LE payload size][protobuf WalRecord]`.
- `IChecksumProvider` — CRC32 over `data` **concatenated with the LSN bytes**; both write and recovery must compute it the same way.
- `IWalRecovery` (`Recovery/WalRecovery.cs`) — crash recovery; touches only the file system, the serializer and the checksum provider.
- `SyncScheduler` (`Sync/SyncScheduler.cs`) — `PeriodicTimer` loop calling back into `WriteAheadLog.FlushAsync`.

`Segments/WalSegmentNaming.cs` is the single source of truth for the `wal-segment-{N}.log` scheme — use it rather than re-deriving names or parsing numbers.

Construct via `await WriteAheadLog.StartAsync(config)`: **initialize the segment manager (which recovers first) → discover `_lastLsn` → start the scheduler.** Recovery runs inside `WalSegmentManager.InitializeAsync`, before it opens a handle, because it rewrites segment files, and `WalSegment` opens them `FileShare.ReadWrite` *without* `FileShare.Delete`, caches the file length at construction, and appends — so repairing under an open handle either fails outright or writes past the torn tail. Tests use the `internal` constructor via `InternalsVisibleTo` when they need fakes.

All public operations serialize on a single non-reentrant `SemaphoreSlim(1,1)`, and the background flush takes the same lock. Two rules follow, and violating either one deadlocks:

- Anything called from inside the lock must not re-acquire it. Read through `ReadCurrentSegmentRecordsAsync`/`ReadRecordsFromFileAsync` (lock-free cores) rather than `ReadAllRecordsAsync` (takes the lock).
- Stop the scheduler *before* taking the lock, never from inside it — see `CloseAsync`.

### Recovery model

A crash can only tear the tail of the log. Recovery scans forward from the oldest segment, validating framing and checksum, and stops at the first record that does not verify: that offset is the end of the log. The valid prefix is kept, everything after it is discarded, and later segments are emptied — the log must stay a *prefix* of what was written, so a bad record is never skipped to salvage records behind it. Recovery lives entirely in `WalRecovery`, and `WalSegmentManager.InitializeAsync` runs it before opening a segment — the segment manager owns the files, so `WriteAheadLog` contains no recovery code at all. It returns a `WalRecoveryReport` saying what each segment's outcome was (`AllLogValid`, `Truncated`, `Emptied`). To repair a log without reopening it, close the WAL and call `WalRecovery.RecoverDirectoryAsync` directly — repair requires that no segment handle is open.

### Things that will bite you

- **`ReadAllRecordsAsync` reads only the current segment file**, not the whole log. Once rotation has happened, earlier records are not returned. Recovery and `DiscoverLastLsnAsync`, by contrast, look at every `wal-segment-*.log`.
- **`wal.config.json` is never read by the library.** `WalConfig.LoadFromFile`/`SaveToFile` exist for callers to use; `StartAsync` only ever sees the `WalConfig` instance it is handed.
- `WalConfig.Directory` is `[JsonIgnore]` — it must be set by the caller, never comes from the config file.
- A segment with no valid prefix is **emptied, not deleted**. Deleting it would renumber segments, `_lastLsn` would reset to 0, and LSNs already on disk would be handed out again. For the same reason `DiscoverLastLsnAsync` falls back from an empty current segment to older ones.
- `ReadAllRecordsAsync` does *not* verify checksums — it trusts that recovery already ran at open. Don't use it as a validation path.
- Segment numbering is parsed out of the file name with `int.Parse`; any other `wal-segment-*.log`-shaped file in the directory will throw.

## Tests

xUnit, in `tests/WalStore.Wal.Tests/`: `WriteAheadLogTests.cs` (write/read/LSN/rotation), `WalRecoveryTests.cs` (one test per corruption scenario, driving `WalRecovery` against hand-built segment files), `WalRecoveryIntegrationTests.cs` (recovery as it happens through `StartAsync`). Each test creates a GUID-named temp directory and deletes it in a `finally`. Corruption tests write bytes into the segment file directly — keep them consistent with the framing and checksum rules above.
