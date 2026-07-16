using System.CommandLine;
using System.Text;
using System.Text.Json;
using WalStore.Wal;

var dirOption = new Option<DirectoryInfo>("--dir", "-d")
{
    Description = "WAL directory path",
    DefaultValueFactory = _ => new DirectoryInfo("./walogs")
};

var fileSizeOption = new Option<long>("--file-size")
{
    Description = "Max segment file size in bytes",
    DefaultValueFactory = _ => 16L * 1024 * 1024
};

var maxSegmentsOption = new Option<int>("--max-segments")
{
    Description = "Max number of segment files",
    DefaultValueFactory = _ => 100
};

var noSyncOption = new Option<bool>("--no-sync")
{
    Description = "Disable force sync to disk",
    DefaultValueFactory = _ => false
};

var syncIntervalOption = new Option<int>("--sync-interval")
{
    Description = "Sync interval in milliseconds",
    DefaultValueFactory = _ => 200
};

// --- write ---
var dataArg = new Argument<string>("data") { Description = "Raw data string to write as a record" };

var writeCommand = new Command("write", "Write a record to the WAL");
writeCommand.Add(dataArg);
writeCommand.Add(dirOption);
writeCommand.Add(fileSizeOption);
writeCommand.Add(maxSegmentsOption);
writeCommand.Add(noSyncOption);
writeCommand.Add(syncIntervalOption);

writeCommand.SetAction(async (ParseResult parseResult) =>
{
    var data = parseResult.GetValue(dataArg);
    var dir = parseResult.GetValue(dirOption);
    var fileSize = parseResult.GetValue(fileSizeOption);
    var maxSegments = parseResult.GetValue(maxSegmentsOption);
    var noSync = parseResult.GetValue(noSyncOption);
    var syncInterval = parseResult.GetValue(syncIntervalOption);

    var config = new WalConfig
    {
        Directory = dir!.FullName,
        MaxFileSize = fileSize,
        MaxSegments = maxSegments,
        EnableForceSync = !noSync,
        SyncIntervalMs = (uint)syncInterval
    };
    await using var wal = await WriteAheadLog.StartAsync(config);
    await wal.WriteRecordAsync(Encoding.UTF8.GetBytes(data!));
    Console.WriteLine($"Written record with data: {data}");
});

// --- read ---
var readCommand = new Command("read", "Read all records from the WAL");
readCommand.Add(dirOption);

readCommand.SetAction(async (ParseResult parseResult) =>
{
    var dir = parseResult.GetValue(dirOption);
    var config = new WalConfig { Directory = dir!.FullName };
    await using var wal = await WriteAheadLog.StartAsync(config);
    var records = await wal.ReadAllRecordsAsync();

    if (records.Count == 0)
    {
        Console.WriteLine("No records found.");
        return;
    }

    foreach (var record in records)
    {
        var dataStr = Encoding.UTF8.GetString(record.Data.ToByteArray());
        Console.WriteLine($"LSN={record.LogSequenceNumber} Timestamp={record.Timestamp} Checksum={record.Checksum} Data={dataStr}");
    }
    Console.WriteLine($"--- Total records: {records.Count} ---");
});

// --- replay ---
var formatOption = new Option<string>("--format", "-f")
{
    Description = "Output format: json (default) or raw",
    DefaultValueFactory = _ => "json"
};

var replayCommand = new Command("replay", "Replay records from the WAL (outputs data field)");
replayCommand.Add(dirOption);
replayCommand.Add(formatOption);

replayCommand.SetAction(async (ParseResult parseResult) =>
{
    var dir = parseResult.GetValue(dirOption);
    var format = parseResult.GetValue(formatOption);
    var config = new WalConfig { Directory = dir!.FullName };
    await using var wal = await WriteAheadLog.StartAsync(config);
    var records = await wal.ReadAllRecordsAsync();

    foreach (var record in records)
    {
        var data = record.Data.ToByteArray();
        if (format == "raw")
            Console.WriteLine(Convert.ToBase64String(data));
        else
            Console.WriteLine(Encoding.UTF8.GetString(data));
    }
});

// --- config ---
var configCommand = new Command("config", "View or modify WAL configuration");

var configShowCommand = new Command("show", "Show current configuration");
configShowCommand.Add(dirOption);

configShowCommand.SetAction((ParseResult parseResult) =>
{
    var dir = parseResult.GetValue(dirOption);
    var configPath = Path.Combine(dir!.FullName, "wal.config.json");
    var config = WalConfig.LoadFromFile(configPath);
    config.Directory = dir.FullName;
    Console.WriteLine(JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
});

var configKeyArg = new Argument<string>("key") { Description = "Config key (max-file-size, max-segments, sync-interval-ms, enable-force-sync)" };
var configValueArg = new Argument<string>("value") { Description = "Config value" };

var configSetCommand = new Command("set", "Set a configuration value");
configSetCommand.Add(configKeyArg);
configSetCommand.Add(configValueArg);
configSetCommand.Add(dirOption);

configSetCommand.SetAction((ParseResult parseResult) =>
{
    var key = parseResult.GetValue(configKeyArg);
    var value = parseResult.GetValue(configValueArg);
    var dir = parseResult.GetValue(dirOption);
    var configPath = Path.Combine(dir!.FullName, "wal.config.json");
    var config = WalConfig.LoadFromFile(configPath);

    switch (key!.ToLowerInvariant())
    {
        case "maxfilesize":
        case "max-file-size":
            config.MaxFileSize = long.Parse(value!);
            break;
        case "maxsegments":
        case "max-segments":
            config.MaxSegments = int.Parse(value!);
            break;
        case "syncintervalms":
        case "sync-interval-ms":
            config.SyncIntervalMs = uint.Parse(value!);
            break;
        case "enableforcesync":
        case "enable-force-sync":
            config.EnableForceSync = bool.Parse(value!);
            break;
        default:
            Console.Error.WriteLine($"Unknown config key: {key}");
            Console.Error.WriteLine("Valid keys: max-file-size, max-segments, sync-interval-ms, enable-force-sync");
            return;
    }

    config.SaveToFile(configPath);
    Console.WriteLine($"Set {key} = {value}");
});

configCommand.Add(configShowCommand);
configCommand.Add(configSetCommand);

// --- root ---
var rootCommand = new RootCommand("Write-Ahead Log CLI - a command-line utility for managing WAL segments");
rootCommand.Add(writeCommand);
rootCommand.Add(readCommand);
rootCommand.Add(replayCommand);
rootCommand.Add(configCommand);

var parseResult = rootCommand.Parse(args);
return await parseResult.InvokeAsync(new InvocationConfiguration());
