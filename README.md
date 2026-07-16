# Write-Ahead Log (WAL) in C#

A .NET 10 command-line utility implementing a Write-Ahead Log with segmentation, protobuf serialization, and periodic sync.

## Features

- **Store log**: Write records in binary format using Google's Protocol Buffers (protobuf)
- **Segmentation**: Log files are segmented based on a fixed size threshold; oldest segments are pruned when the max count is exceeded
- **CRC32 checksums**: Each record includes a CRC32 checksum for data integrity (computed over data + LSN)
- **Periodic background sync**: A background task flushes buffered data to disk at a configurable interval
- **Force sync**: Optional OS-level `FlushFileBuffers` after every flush for crash safety
- **Async concurrency**: Thread-safe writes via `SemaphoreSlim`
- **Crash recovery**: On restart, WAL resumes from the last segment and continues LSN numbering
- **Config persistence**: WAL settings can be saved/loaded from a JSON config file

## Architecture

The library follows an object-oriented design with 5 core interfaces and concrete implementations separated by concern:

```
src/WalStore.Wal/
├── Protos/wal_record.proto               # Protobuf record schema
├── Contracts/
│   ├── IWalLogger.cs                     # Public WAL API
│   ├── IWalSegment.cs                    # Single segment file abstraction
│   ├── IWalSegmentManager.cs             # Segment lifecycle
│   ├── IWalRecordSerializer.cs           # Record binary format
│   └── IChecksumProvider.cs              # CRC32 computation
├── Config/WalConfig.cs                   # WAL settings + JSON persistence
├── Segments/
│   ├── WalSegment.cs                     # FileStream wrapper → IWalSegment
│   └── WalSegmentManager.cs             # Discovery, rotation, cleanup → IWalSegmentManager
├── Serialization/WalRecordSerializer.cs  # [int32 size][protobuf] → IWalRecordSerializer
├── Checksum/Crc32ChecksumProvider.cs     # System.IO.Hashing → IChecksumProvider
├── Sync/SyncScheduler.cs                 # PeriodicTimer loop (extracted concern)
└── WriteAheadLog.cs                      # Thin orchestrator wiring the components
```

`WriteAheadLog` is the thin orchestrator — it delegates to `IWalSegmentManager` for file I/O,
`IWalRecordSerializer` for record format, `IChecksumProvider` for integrity, and
`SyncScheduler` for periodic flushing. Each component can be mocked and tested in isolation.

```
src/WalStore.Cli/         # CLI entry point
tests/WalStore.Wal.Tests/ # xUnit tests
```

## CLI Usage

```
WalStore.Cli [command] [options]
```

### Commands

| Command | Description |
|---|---|
| `write <data>` | Write a record to the WAL |
| `read` | Read all records from the WAL |
| `replay` | Replay records (outputs the data field) |
| `config` | View or modify WAL configuration |

### write

```
WalStore.Cli write <data> [options]
```

Arguments:
- `data` — Raw data string to write as a record

Options:
- `-d, --dir <dir>` — WAL directory path (default: `./walogs`)
- `--file-size <bytes>` — Max segment file size (default: 16777216 / 16 MB)
- `--max-segments <n>` — Max number of segment files to retain (default: 100)
- `--no-sync` — Disable force sync to disk
- `--sync-interval <ms>` — Sync interval in milliseconds (default: 200)

Example:
```
WalStore.Cli write "hello world" -d /tmp/walogs
```

### read

```
WalStore.Cli read [options]
```

Options:
- `-d, --dir <dir>` — WAL directory path (default: `./walogs`)

Example:
```
WalStore.Cli read -d /tmp/walogs
```

Output:
```
LSN=1 Timestamp=8579145384376141252 Checksum=631844488 Data=hello world
--- Total records: 1 ---
```

### replay

```
WalStore.Cli replay [options]
```

Options:
- `-d, --dir <dir>` — WAL directory path (default: `./walogs`)
- `-f, --format <format>` — Output format: `json` (default) or `raw` (base64)

Example:
```
WalStore.Cli replay -d /tmp/walogs --format json
```

### config

```
WalStore.Cli config [command] [options]
```

Subcommands:
- `show` — Display current configuration
- `set <key> <value>` — Update a configuration value

Valid config keys: `max-file-size`, `max-segments`, `sync-interval-ms`, `enable-force-sync`

Example:
```
WalStore.Cli config show -d /tmp/walogs
WalStore.Cli config set max-file-size 8388608 -d /tmp/walogs
```

## Record Format (protobuf)

```protobuf
message WalRecord {
    bytes  data                = 3;
    uint64 log_sequence_number = 1;
    int64  timestamp           = 2;  // milliseconds since Unix epoch
    uint32 checksum            = 4;  // CRC32 of data + LSN
}
```

Each record is stored on disk as `[int32 little-endian size][protobuf bytes]`.

## Segment File Naming

```
wal-segment-{N}.log
```

Segments are numbered sequentially starting from 1. When the current segment exceeds `max-file-size`, a new segment is created. If the segment count exceeds `max-segments`, the oldest segment is deleted.

## Tests

```
dotnet test
```

| Test | What it verifies |
|---|---|
| `WriteAndReadRecords` | Write N records, close, reopen, read back — data matches |
| `LogSequenceNumberIncrements` | LSN starts at 0, increments by 1 per write |
| `ChecksumIsPresentOnRecords` | Each record has a non-zero CRC32 checksum |
| `SegmentRotation` | Small max size forces rotation; old segments pruned at max count |
| `ReadFromEmptyWalReturnsEmptyList` | Fresh WAL returns empty list on read |
| `LsnSurvivesRestart` | LSN counter persists across WAL close/reopen cycles |
