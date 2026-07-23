using System.Text.Json;

namespace ClipSync;

// Configuration locale, persistée dans %APPDATA%\ClipSync\config.json.
// MVP : valeurs de démo par défaut pour se connecter au serveur local.
public sealed class Config
{
    public string ServerUrl { get; set; } = "ws://localhost:8787/ws";
    public string HttpUrl { get; set; } = "http://localhost:8787";
    public string AccountId { get; set; } = "";
    public string Secret { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string DeviceName { get; set; } = Environment.MachineName;

    static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClipSync", "config.json");

    public static Config Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<Config>(File.ReadAllText(FilePath));
                if (loaded is not null) return loaded.EnsureDefaults();
            }
        }
        catch { /* fichier corrompu : on repart d'une config neuve */ }

        return new Config().EnsureDefaults();
    }

    Config EnsureDefaults()
    {
        // MVP local : un compte de démo partagé, un deviceId stable par machine.
        if (string.IsNullOrEmpty(AccountId)) AccountId = "demo-account";
        if (string.IsNullOrEmpty(Secret)) Secret = "demo-secret";
        if (string.IsNullOrEmpty(DeviceId)) DeviceId = "pc-" + Guid.NewGuid().ToString("N")[..8];
        if (string.IsNullOrEmpty(DeviceName)) DeviceName = Environment.MachineName;
        Save();
        return this;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best effort */ }
    }
}
