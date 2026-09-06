using System.Text.Json;
using System.Text.Json.Serialization;

namespace WalStore.Wal;

public class WalConfig
{
    public long MaxFileSize { get; set; } = 16 * 1024 * 1024; // 16 MB
    public int MaxSegments { get; set; } = 100;
    public bool ForceSyncEnabled { get; set; } = true;
    public uint SyncIntervalMs { get; set; } = 200;

    [JsonIgnore]
    public string Directory { get; set; } = string.Empty;

    public static WalConfig LoadFromFile(string configPath)
    {
        if (!File.Exists(configPath))
            return new WalConfig();

        var json = File.ReadAllText(configPath);
        return JsonSerializer.Deserialize<WalConfig>(json) ?? new WalConfig();
    }

    public void SaveToFile(string configPath)
    {
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(configPath, json);
    }
}
