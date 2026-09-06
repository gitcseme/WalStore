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

`WriteAheadLog` (`src/WalStore.Wal/WriteAheadLog.cs`) is an orchestrator only — every mechanism is behind one of the five interfaces in `Contracts/` and injected through the `internal` constructor:

- `IWalSegmentManager` (`Segments/WalSegmentManager.cs`) — discovers `wal-segment-{N}.log` files, owns `CurrentSegment`, rotates and prunes the oldest segment once `N > MaxSegments`.
- `IWalSegment` (`Segments/WalSegment.cs`) — a write-only `FileStream` plus a `BytesWritten` counter (seeded from `FileInfo.Length` when opened `FileMode.Append`). `ForceSync()` is `Flush(true)`.
- `IWalRecordSerializer` — on-disk framing is `[int32 LE payload size][protobuf WalRecord]`.
- `IChecksumProvider` — CRC32 over `data` **concatenated with the LSN bytes**; both write and recovery must compute it the same way.
- `SyncScheduler` (`Sync/SyncScheduler.cs`) — `PeriodicTimer` loop calling back into `WriteAheadLog.FlushAsync`.

Construct via `await WriteAheadLog.StartAsync(config)`, which wires the concrete implementations, initializes the segment manager, recovers `_lastLsn` by reading the log, and starts the sync scheduler. Tests use the `internal` constructor via `InternalsVisibleTo` when they need fakes.

All public operations serialize on a single `SemaphoreSlim(1,1)`. The background flush takes the same lock, so anything called from inside the lock must not re-acquire it (`DiscoverLastLsnAsync` deliberately calls `ReadAllRecordsAsync` *before* the scheduler starts, outside the lock).

### Things that will bite you

- **`ReadAllRecordsAsync` reads only the current segment file**, not the whole log. Once rotation has happened, earlier records are not returned. `RecoverAsync`, by contrast, walks every `wal-segment-*.log`.
- **`wal.config.json` is never read by the library.** `WalConfig.LoadFromFile`/`SaveToFile` exist for callers to use; `StartAsync` only ever sees the `WalConfig` instance it is handed.
- `WalConfig.Directory` is `[JsonIgnore]` — it must be set by the caller, never comes from the config file.
- Recovery is truncation-based: it stops at the first record whose size, protobuf parse, or checksum fails and rewrites the file up to the end of the last valid record (deleting the file if nothing was valid). It does not attempt to skip a bad record and keep later ones.
- Segment numbering is parsed out of the file name with `int.Parse`; any other `wal-segment-*.log`-shaped file in the directory will throw.

## Tests

xUnit, `tests/WalStore.Wal.Tests/WriteAheadLogTests.cs`. Each test creates a GUID-named temp directory and deletes it in a `finally`. Corruption tests write bytes into the segment file directly, then assert on `RecoverAsync` behaviour — keep them consistent with the framing and checksum rules above.
