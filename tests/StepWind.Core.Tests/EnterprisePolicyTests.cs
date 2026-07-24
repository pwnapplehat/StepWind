using System.Text.Json;
using StepWind.Core.Engine;
using StepWind.Core.Enterprise;
using StepWind.Core.Ipc;
using StepWind.Core.Storage;
using Xunit;

namespace StepWind.Core.Tests;

/// <summary>
/// Enterprise machine policy: an administrator's Group Policy / MDM settings are enforced by the
/// service and bind every caller — including a local administrator — because only the org's policy
/// (which writes HKLM\SOFTWARE\Policies) may change a managed setting. These pin the pure policy
/// model and its enforcement through the real host.
/// </summary>
public class EnterprisePolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sw-policy", Guid.NewGuid().ToString("N"));

    private static readonly string StrangerSid = "S-1-5-21-9999999999-8888888888-7777777777-4321";
    private static CallerContext Admin => new() { UserSid = StrangerSid, UserName = "admin", IsAdministrator = true };
    private static CallerContext User => new() { UserSid = StrangerSid, UserName = "user" };

    public EnterprisePolicyTests() => Directory.CreateDirectory(_root);

    private StepWindSettings Settings(params string[] folders) => new()
    {
        StoreRoot = Path.Combine(_root, "store"),
        WatchedFolders = [.. folders],
        FlightRecorderEnabled = false,
    };

    // ── Pure model ──

    [Fact]
    public void None_is_unmanaged_and_locks_nothing()
    {
        Assert.False(MachinePolicy.None.Managed);
        Assert.Empty(MachinePolicy.None.LockedKeys);
        Assert.True(MachinePolicy.None.AllowUserFolderChanges);
    }

    [Fact]
    public void Enforce_overlays_values_and_unions_mandatory_folders()
    {
        string mustProtect = Path.Combine(_root, "Mandated");
        Directory.CreateDirectory(mustProtect);
        var policy = new MachinePolicy
        {
            AutoUpdateEnabled = false,
            RetentionMaxVersionsPerFile = 7,
            MandatoryFolders = [mustProtect],
        };
        var s = Settings();
        s.AutoUpdateEnabled = true; // will be overridden

        Assert.True(policy.Enforce(s));
        Assert.False(s.AutoUpdateEnabled);                        // enforced value won
        Assert.Equal(7, s.Retention.MaxVersionsPerFile);
        Assert.Contains(mustProtect, s.WatchedFolders, StringComparer.OrdinalIgnoreCase);
        Assert.True(policy.Locks("AutoUpdateEnabled"));
        Assert.True(policy.Locks("RetentionMaxVersionsPerFile"));
        Assert.False(policy.Locks("EncryptionEnabled"));
        Assert.True(policy.IsMandatoryFolder(mustProtect));
    }

    [Fact]
    public void FromKey_parses_every_registry_value_type_correctly()
    {
        // Verifies the REAL registry adapter (DWORD/QWORD/REG_SZ/REG_MULTI_SZ -> typed policy)
        // against an actual key — written under HKCU so it needs no elevation and no HKLM writes,
        // but exercises the exact Microsoft.Win32 parsing the service uses against HKLM in prod.
        string sub = @"Software\StepWindTest\" + Guid.NewGuid().ToString("N");
        using (Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(sub)!)
        {
            key.SetValue("AutoUpdateEnabled", 0, Microsoft.Win32.RegistryValueKind.DWord);
            key.SetValue("EncryptionEnabled", 1, Microsoft.Win32.RegistryValueKind.DWord);
            key.SetValue("RetentionMaxVersionsPerFile", 42, Microsoft.Win32.RegistryValueKind.DWord);
            key.SetValue("MinFreeDiskBytes", "10737418240", Microsoft.Win32.RegistryValueKind.String);   // REG_SZ > 4 GB
            key.SetValue("MaxStoreBytes", 5368709120L, Microsoft.Win32.RegistryValueKind.QWord);          // REG_QWORD
            key.SetValue("MandatoryFolders", new[] { @"C:\Work", @"D:\Shared" }, Microsoft.Win32.RegistryValueKind.MultiString);
            key.SetValue("AllowUserFolderChanges", 0, Microsoft.Win32.RegistryValueKind.DWord);
        }

        try
        {
            using Microsoft.Win32.RegistryKey read = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(sub)!;
            MachinePolicy p = MachinePolicy.FromKey(read);

            Assert.False(p.AutoUpdateEnabled);
            Assert.True(p.EncryptionEnabled);
            Assert.Equal(42, p.RetentionMaxVersionsPerFile);
            Assert.Equal(10737418240L, p.MinFreeDiskBytes);   // parsed from REG_SZ, no 32-bit overflow
            Assert.Equal(5368709120L, p.MaxStoreBytes);       // parsed from REG_QWORD
            Assert.Equal([@"C:\Work", @"D:\Shared"], p.MandatoryFolders);
            Assert.False(p.AllowUserFolderChanges);
            Assert.True(p.Managed);
            Assert.True(p.Locks("AutoUpdateEnabled"));
            Assert.Null(p.RespectGitIgnore);                  // absent value stays "not configured"
        }
        finally
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(sub, throwOnMissingSubKey: false);
        }
    }

    // ── Enforcement through the host ──

    [Fact]
    public void A_policy_locked_setting_cannot_be_changed_even_by_an_admin()
    {
        var policy = new MachinePolicy { AutoUpdateEnabled = false };
        var audit = new CapturingAudit();
        using var host = new StepWindHost(Settings(), new GzipBlobCodec(), policy: policy, audit: audit);

        // Startup enforced the policy value.
        SettingsView s0 = ReadSettings(host);
        Assert.False(s0.AutoUpdateEnabled);
        Assert.True(s0.Managed);
        Assert.Contains("AutoUpdateEnabled", s0.ManagedKeys);

        // Even an administrator can't flip it through the app.
        IpcResponse denied = host.Handle(new IpcRequest
        {
            Command = IpcCommand.SetSettings,
            Arg1 = JsonSerializer.Serialize(new { AutoUpdateEnabled = true }),
        }, Admin);

        Assert.False(denied.Ok);
        Assert.Contains("managed by your organization", denied.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.False(ReadSettings(host).AutoUpdateEnabled); // unchanged
        Assert.Contains(audit.Events, e => e.Action == AuditAction.SettingsChangeDeniedByPolicy);
    }

    [Fact]
    public void Echoing_a_locked_settings_current_value_is_not_refused()
    {
        var policy = new MachinePolicy { AutoUpdateEnabled = false };
        using var host = new StepWindHost(Settings(), new GzipBlobCodec(), policy: policy);

        // The GUI patches whole sections, so it re-sends the enforced value — that's not a change.
        IpcResponse ok = host.Handle(new IpcRequest
        {
            Command = IpcCommand.SetSettings,
            Arg1 = JsonSerializer.Serialize(new { AutoUpdateEnabled = false }),
        }, Admin);
        Assert.True(ok.Ok, ok.Error);
    }

    [Fact]
    public void A_mandatory_folder_cannot_be_unprotected()
    {
        string mandated = Path.Combine(_root, "Mandated");
        string optional = Path.Combine(_root, "Optional");
        Directory.CreateDirectory(mandated);
        Directory.CreateDirectory(optional);
        var policy = new MachinePolicy { MandatoryFolders = [mandated] };
        using var host = new StepWindHost(Settings(optional), new GzipBlobCodec(), policy: policy);

        // Startup force-added the mandatory folder alongside the user's optional one.
        Assert.Contains(mandated, ReadSettings(host).WatchedFolders, StringComparer.OrdinalIgnoreCase);

        // Trying to drop the mandatory folder (keeping only the optional one) is refused…
        IpcResponse denied = host.Handle(new IpcRequest
        {
            Command = IpcCommand.SetSettings,
            Arg1 = JsonSerializer.Serialize(new { WatchedFolders = new[] { optional } }),
        }, Admin);
        Assert.False(denied.Ok);
        Assert.Contains("required by your organization", denied.Error!, StringComparison.OrdinalIgnoreCase);

        // …but dropping only the OPTIONAL one (keeping the mandatory) is fine.
        IpcResponse ok = host.Handle(new IpcRequest
        {
            Command = IpcCommand.SetSettings,
            Arg1 = JsonSerializer.Serialize(new { WatchedFolders = new[] { mandated } }),
        }, Admin);
        Assert.True(ok.Ok, ok.Error);
    }

    [Fact]
    public void Locking_folder_changes_blocks_non_admins_only()
    {
        string existing = Path.Combine(_root, "Existing");
        string wanted = Path.Combine(_root, "Wanted");
        Directory.CreateDirectory(existing);
        Directory.CreateDirectory(wanted);
        var policy = new MachinePolicy { AllowUserFolderChanges = false };
        using var host = new StepWindHost(Settings(existing), new GzipBlobCodec(), policy: policy);

        // A standard user can't change the folder set…
        IpcResponse denied = host.Handle(new IpcRequest
        {
            Command = IpcCommand.SetSettings,
            Arg1 = JsonSerializer.Serialize(new { WatchedFolders = new[] { existing, wanted } }),
        }, User);
        Assert.False(denied.Ok);
        Assert.Contains("managed by your organization", denied.Error!, StringComparison.OrdinalIgnoreCase);

        // …but an administrator still can.
        IpcResponse ok = host.Handle(new IpcRequest
        {
            Command = IpcCommand.SetSettings,
            Arg1 = JsonSerializer.Serialize(new { WatchedFolders = new[] { existing, wanted } }),
        }, Admin);
        Assert.True(ok.Ok, ok.Error);
    }

    private static SettingsView ReadSettings(StepWindHost host)
    {
        IpcResponse r = host.Handle(new IpcRequest { Command = IpcCommand.GetSettings });
        return JsonSerializer.Deserialize<SettingsView>(r.Json!)!;
    }

    private sealed class SettingsView
    {
        public bool AutoUpdateEnabled { get; set; }
        public List<string> WatchedFolders { get; set; } = [];
        public bool Managed { get; set; }
        public string[] ManagedKeys { get; set; } = [];
        public bool AllowUserFolderChanges { get; set; }
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}

/// <summary>Captures audit records in memory so tests can assert what was logged.</summary>
internal sealed class CapturingAudit : IAuditSink
{
    public List<AuditEvent> Events { get; } = [];

    public void Write(AuditEvent e) => Events.Add(e);
}
