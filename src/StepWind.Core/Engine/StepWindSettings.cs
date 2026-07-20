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

    /// <summary>Automatic silent updates, applied by the SYSTEM service (no UAC). Default on.</summary>
    public bool AutoUpdateEnabled { get; set; } = true;

    /// <summary>
    /// Encrypt the store at rest (AES-256-GCM; random key sealed with machine-scope DPAPI by
    /// <see cref="KeyProtector"/> so the unattended service needs no passphrase).
    /// </summary>
    public bool EncryptionEnabled { get; set; }

    /// <summary>Largest single file to version, bytes (0 = unlimited).</summary>
    public long MaxFileBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    public RetentionPolicy Retention { get; set; } = new();

    /// <summary>
    /// True once a human has made any protected-folders decision. The GUI seeds default
    /// folders ONLY while this is false — without it, a user who deliberately removed
    /// everything got the defaults silently re-added on the next launch (the "I removed
    /// Desktop but it came back" bug).
    /// </summary>
    public bool FirstRunCompleted { get; set; }

    /// <summary>Timeline display scope: true = only operations inside protected folders.</summary>
    public bool TimelineProtectedOnly { get; set; }

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

    /// <summary>
    /// First-run defaults. Deliberately NO watched folders: the service runs as LocalSystem,
    /// whose "Documents"/"Desktop" are the system profile's, not the logged-in user's — so
    /// picking folders here would watch the wrong (or non-existent) paths. The GUI runs as the
    /// real user, computes their actual work folders via <see cref="DefaultUserFolders"/>, and
    /// pushes them to the service over IPC on first run.
    /// </summary>
    public static StepWindSettings CreateDefault() => new();

    /// <summary>The current user's real work folders — call this from the GUI, not the service.</summary>
    public static List<string> DefaultUserFolders()
    {
        var folders = new List<string>();
        foreach (Environment.SpecialFolder f in new[]
        {
            Environment.SpecialFolder.MyDocuments,
            Environment.SpecialFolder.Desktop,
            Environment.SpecialFolder.MyPictures,
        })
        {
            string path = Environment.GetFolderPath(f);
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path)
                && !folders.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                folders.Add(path);
            }
        }

        return folders;
    }
}
