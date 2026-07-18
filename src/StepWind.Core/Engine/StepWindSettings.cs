using System.IO;
using System.Text.Json;
using StepWind.Core.Storage;

namespace StepWind.Core.Engine;

/// <summary>
/// Persisted configuration for the whole product (service + GUI read the same file under
/// %ProgramData%\StepWind so both privilege levels agree). Corrupt/missing settings fall back
/// to safe defaults — the protector must always start.
/// </summary>
public sealed class StepWindSettings
{
    /// <summary>Folders whose file contents get full version history (the time-machine layer).</summary>
    public List<string> WatchedFolders { get; set; } = [];

    /// <summary>Absolute path prefixes to never version (games, huge data dirs…).</summary>
    public List<string> ExcludedPrefixes { get; set; } = [];

    /// <summary>Where blobs + the version log live. Default: %ProgramData%\StepWind\store.</summary>
    public string StoreRoot { get; set; } = DefaultStoreRoot;

    /// <summary>Whole-machine operation flight recorder (USN + ETW). Needs the service.</summary>
    public bool FlightRecorderEnabled { get; set; } = true;

    /// <summary>Encrypt the store with a passphrase (key derived; salt stored in the repo).</summary>
    public bool EncryptionEnabled { get; set; }

    /// <summary>Largest single file to version, bytes (0 = unlimited).</summary>
    public long MaxFileBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    public RetentionPolicy Retention { get; set; } = new();

    public static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "StepWind");

    public static string DefaultStoreRoot => Path.Combine(DefaultRoot, "store");

    private static string SettingsPath => Path.Combine(DefaultRoot, "settings.json");

    public static StepWindSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                StepWindSettings? s = JsonSerializer.Deserialize<StepWindSettings>(File.ReadAllText(SettingsPath));
                if (s is not null)
                {
                    return s;
                }
            }
        }
        catch
        {
            // fall through to defaults
        }

        return CreateDefault();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DefaultRoot);
            string tmp = SettingsPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, SettingsPath, overwrite: true);
        }
        catch
        {
            // best effort
        }
    }

    /// <summary>Sensible first-run defaults: watch the user's real work folders.</summary>
    public static StepWindSettings CreateDefault()
    {
        var s = new StepWindSettings();
        foreach (Environment.SpecialFolder f in new[]
        {
            Environment.SpecialFolder.MyDocuments,
            Environment.SpecialFolder.Desktop,
            Environment.SpecialFolder.MyPictures,
        })
        {
            string path = Environment.GetFolderPath(f);
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
            {
                s.WatchedFolders.Add(path);
            }
        }

        return s;
    }
}
