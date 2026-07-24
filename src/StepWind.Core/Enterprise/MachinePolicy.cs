using System.Runtime.Versioning;
using Microsoft.Win32;
using StepWind.Core.Engine;

namespace StepWind.Core.Enterprise;

/// <summary>
/// Machine-wide policy set by an administrator (Group Policy / Intune / MDM) under
/// <c>HKLM\SOFTWARE\Policies\StepWind</c> — the standard managed-policy hive, writable only by
/// administrators and the domain/MDM policy engine. A value being PRESENT means the setting is
/// ENFORCED: the service applies it and refuses to let any user (owner OR local admin) change it
/// through the app, because only the org's policy may. This is the enterprise control surface,
/// and in a managed fleet it fully removes the per-user shared-PC ambiguity — machine-wide
/// settings become policy-controlled, not user-controlled.
///
/// The type is a pure, immutable snapshot so it's trivially unit-testable; <see cref="FromRegistry"/>
/// is the only Windows-specific part.
/// </summary>
public sealed class MachinePolicy
{
    public const string PolicyKeyPath = @"SOFTWARE\Policies\StepWind";

    // Each nullable value: null = "not configured" (user-controllable); set = enforced + locked.
    public bool? EncryptionEnabled { get; init; }
    public bool? EncryptIndex { get; init; }
    public bool? AutoUpdateEnabled { get; init; }
    public bool? FlightRecorderEnabled { get; init; }
    public bool? RespectGitIgnore { get; init; }
    public long? MinFreeDiskBytes { get; init; }
    public long? MaxStoreBytes { get; init; }
    public int? RetentionKeepAllHours { get; init; }
    public int? RetentionHourlyDays { get; init; }
    public int? RetentionDailyDays { get; init; }
    public int? RetentionMaxAgeDays { get; init; }
    public int? RetentionMaxVersionsPerFile { get; init; }

    /// <summary>Folders the org forces-protect; always present in the watch set and not user-removable.</summary>
    public IReadOnlyList<string> MandatoryFolders { get; init; } = [];

    /// <summary>False = only administrators may add/remove protected folders (default true).</summary>
    public bool AllowUserFolderChanges { get; init; } = true;

    /// <summary>Write the security audit log to the Windows Event Log (default true).</summary>
    public bool AuditEnabled { get; init; } = true;

    /// <summary>Nothing configured — the unmanaged default every non-enterprise machine sees.</summary>
    public static MachinePolicy None { get; } = new();

    /// <summary>True if any policy value is configured (the machine is "managed").</summary>
    public bool Managed =>
        EncryptionEnabled is not null || EncryptIndex is not null || AutoUpdateEnabled is not null
        || FlightRecorderEnabled is not null || RespectGitIgnore is not null
        || MinFreeDiskBytes is not null || MaxStoreBytes is not null
        || RetentionKeepAllHours is not null || RetentionHourlyDays is not null
        || RetentionDailyDays is not null || RetentionMaxAgeDays is not null
        || RetentionMaxVersionsPerFile is not null
        || MandatoryFolders.Count > 0 || !AllowUserFolderChanges;

    /// <summary>Setting keys (matching <c>SettingsPatch</c>/the IPC contract) that policy has locked.</summary>
    public IReadOnlySet<string> LockedKeys
    {
        get
        {
            var s = new HashSet<string>(StringComparer.Ordinal);
            if (EncryptionEnabled is not null) s.Add("EncryptionEnabled");
            if (EncryptIndex is not null) s.Add("EncryptIndex");
            if (AutoUpdateEnabled is not null) s.Add("AutoUpdateEnabled");
            if (FlightRecorderEnabled is not null) s.Add("FlightRecorderEnabled");
            if (RespectGitIgnore is not null) s.Add("RespectGitIgnore");
            if (MinFreeDiskBytes is not null) s.Add("MinFreeDiskBytes");
            if (MaxStoreBytes is not null) s.Add("MaxStoreBytes");
            if (RetentionKeepAllHours is not null) s.Add("RetentionKeepAllHours");
            if (RetentionHourlyDays is not null) s.Add("RetentionHourlyDays");
            if (RetentionDailyDays is not null) s.Add("RetentionDailyDays");
            if (RetentionMaxAgeDays is not null) s.Add("RetentionMaxAgeDays");
            if (RetentionMaxVersionsPerFile is not null) s.Add("RetentionMaxVersionsPerFile");
            return s;
        }
    }

    /// <summary>True if the named setting key is policy-enforced and must not be user-changed.</summary>
    public bool Locks(string settingKey) => LockedKeys.Contains(settingKey);

    /// <summary>
    /// Overlays every enforced value onto <paramref name="s"/> so the running configuration
    /// reflects policy. Mandatory folders are unioned into the watch set. Called at service start
    /// and whenever policy could have changed, BEFORE the watch engine / schedules read settings.
    /// Returns true if it changed anything (so the caller can persist / rebuild).
    /// </summary>
    public bool Enforce(StepWindSettings s)
    {
        bool changed = false;
        void SetBool(bool? p, Func<bool> get, Action<bool> set)
        {
            if (p is bool b && get() != b) { set(b); changed = true; }
        }

        SetBool(EncryptionEnabled, () => s.EncryptionEnabled, v => s.EncryptionEnabled = v);
        SetBool(EncryptIndex, () => s.EncryptIndex, v => s.EncryptIndex = v);
        SetBool(AutoUpdateEnabled, () => s.AutoUpdateEnabled, v => s.AutoUpdateEnabled = v);
        SetBool(FlightRecorderEnabled, () => s.FlightRecorderEnabled, v => s.FlightRecorderEnabled = v);
        SetBool(RespectGitIgnore, () => s.RespectGitIgnore, v => s.RespectGitIgnore = v);

        if (MinFreeDiskBytes is long mf && s.MinFreeDiskBytes != mf) { s.MinFreeDiskBytes = mf; changed = true; }
        if (MaxStoreBytes is long ms && s.MaxStoreBytes != ms) { s.MaxStoreBytes = ms; changed = true; }
        if (RetentionKeepAllHours is int ka && s.Retention.KeepAllHours != ka) { s.Retention.KeepAllHours = ka; changed = true; }
        if (RetentionHourlyDays is int hd && s.Retention.HourlyDays != hd) { s.Retention.HourlyDays = hd; changed = true; }
        if (RetentionDailyDays is int dd && s.Retention.DailyDays != dd) { s.Retention.DailyDays = dd; changed = true; }
        if (RetentionMaxAgeDays is int ma && s.Retention.MaxAgeDays != ma) { s.Retention.MaxAgeDays = ma; changed = true; }
        if (RetentionMaxVersionsPerFile is int mv && s.Retention.MaxVersionsPerFile != mv) { s.Retention.MaxVersionsPerFile = mv; changed = true; }

        foreach (string folder in MandatoryFolders)
        {
            string trimmed = folder.TrimEnd('\\', '/');
            if (trimmed.Length > 0 && !s.WatchedFolders.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            {
                s.WatchedFolders.Add(trimmed);
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>True if the folder is one the org forces-protect (so it may not be un-protected).</summary>
    public bool IsMandatoryFolder(string folder)
    {
        string t = folder.TrimEnd('\\', '/');
        foreach (string m in MandatoryFolders)
        {
            if (string.Equals(m.TrimEnd('\\', '/'), t, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Reads policy from <c>HKLM\SOFTWARE\Policies\StepWind</c>. Absent key / unreadable values =
    /// <see cref="None"/> (nothing enforced) — policy is purely additive, never a way to break an
    /// unmanaged machine.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static MachinePolicy FromRegistry()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(PolicyKeyPath, writable: false);
            return FromKey(key);
        }
        catch (Exception)
        {
            return None; // never let a malformed policy break the protector
        }
    }

    /// <summary>
    /// Builds a policy from an already-opened registry key (null = <see cref="None"/>). Split out
    /// from <see cref="FromRegistry"/> so the real value-parsing (DWORD/QWORD/REG_SZ/MULTI_SZ →
    /// typed policy) is unit-testable against a user-hive key, with no elevation and no HKLM writes.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static MachinePolicy FromKey(RegistryKey? key)
    {
        if (key is null)
        {
            return None;
        }

        return new MachinePolicy
        {
            EncryptionEnabled = ReadBool(key, "EncryptionEnabled"),
            EncryptIndex = ReadBool(key, "EncryptIndex"),
            AutoUpdateEnabled = ReadBool(key, "AutoUpdateEnabled"),
            FlightRecorderEnabled = ReadBool(key, "FlightRecorderEnabled"),
            RespectGitIgnore = ReadBool(key, "RespectGitIgnore"),
            MinFreeDiskBytes = ReadLong(key, "MinFreeDiskBytes"),
            MaxStoreBytes = ReadLong(key, "MaxStoreBytes"),
            RetentionKeepAllHours = ReadInt(key, "RetentionKeepAllHours"),
            RetentionHourlyDays = ReadInt(key, "RetentionHourlyDays"),
            RetentionDailyDays = ReadInt(key, "RetentionDailyDays"),
            RetentionMaxAgeDays = ReadInt(key, "RetentionMaxAgeDays"),
            RetentionMaxVersionsPerFile = ReadInt(key, "RetentionMaxVersionsPerFile"),
            MandatoryFolders = ReadMultiString(key, "MandatoryFolders"),
            AllowUserFolderChanges = ReadBool(key, "AllowUserFolderChanges") ?? true,
            AuditEnabled = ReadBool(key, "AuditEnabled") ?? true,
        };
    }

    [SupportedOSPlatform("windows")]
    private static bool? ReadBool(RegistryKey key, string name)
        => ReadInt(key, name) is int i ? i != 0 : null;

    [SupportedOSPlatform("windows")]
    private static int? ReadInt(RegistryKey key, string name)
        => key.GetValue(name) is { } v && int.TryParse(Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture), out int i) ? i : null;

    [SupportedOSPlatform("windows")]
    private static long? ReadLong(RegistryKey key, string name)
        => key.GetValue(name) is { } v && long.TryParse(Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture), out long l) ? l : null;

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<string> ReadMultiString(RegistryKey key, string name)
        => key.GetValue(name) is string[] arr
            ? [.. arr.Where(s => !string.IsNullOrWhiteSpace(s))]
            : [];
}
