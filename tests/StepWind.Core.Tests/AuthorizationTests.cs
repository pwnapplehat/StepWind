using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.Json;
using StepWind.Core.Engine;
using StepWind.Core.Ipc;
using StepWind.Core.Storage;
using Xunit;

namespace StepWind.Core.Tests;

/// <summary>
/// P0-A/P0-F: the elevated service acts on behalf of whoever connects, so it must authorize
/// every private or destructive action against the caller's identity. These tests pin the
/// security properties a multi-user machine depends on: one local user cannot read, restore, or
/// purge another user's history, cannot reverse another user's file operations as SYSTEM, and
/// cannot forge an operation the server never recorded.
/// </summary>
[SupportedOSPlatform("windows")]
public class AuthorizationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "stepwind-authz", Guid.NewGuid().ToString("N"));
    private readonly string _watch;
    private readonly StepWindHost _host;
    private readonly string _mySid;

    private static readonly string StrangerSid = "S-1-5-21-1111111111-2222222222-3333333333-4444";

    public AuthorizationTests()
    {
        _mySid = WindowsIdentity.GetCurrent().User!.Value;
        _watch = Path.Combine(_root, "Docs");
        Directory.CreateDirectory(_watch);
        var settings = new StepWindSettings
        {
            StoreRoot = Path.Combine(_root, "store"),
            WatchedFolders = [_watch],
            FlightRecorderEnabled = false,
            // "Docs" is owned by the current test user (me); a stranger owns nothing here.
            RootOwners = new(StringComparer.OrdinalIgnoreCase) { ["Docs"] = [_mySid] },
        };
        _host = new StepWindHost(settings, new GzipBlobCodec());
    }

    // Non-privileged callers with a real SID but no live pipe handle (so the "can you read the
    // folder yourself" safety net can't apply — access must come purely from recorded ownership).
    private CallerContext Owner => new() { UserSid = _mySid, UserName = "me" };
    private CallerContext Stranger => new() { UserSid = StrangerSid, UserName = "someone-else" };
    private CallerContext Admin => new() { UserSid = StrangerSid, UserName = "admin", IsAdministrator = true };

    private async Task<string> CaptureAFile()
    {
        string file = Path.Combine(_watch, "private.txt");
        File.WriteAllText(file, "another user's secret notes");
        for (int i = 0; i < 40; i++)
        {
            if (_host.Handle(new IpcRequest { Command = IpcCommand.GetHistory, Arg1 = "Docs/private.txt" }) is { Ok: true } h
                && JsonSerializer.Deserialize<VersionEntry[]>(h.Json!)!.Length > 0)
            {
                return file;
            }

            await Task.Delay(250);
        }

        throw new Xunit.Sdk.XunitException("file was never captured");
    }

    [Fact]
    public async Task A_stranger_cannot_read_another_users_file_history()
    {
        await CaptureAFile();

        IpcResponse owner = _host.Handle(new IpcRequest { Command = IpcCommand.GetHistory, Arg1 = "Docs/private.txt" }, Owner);
        Assert.True(owner.Ok, owner.Error);
        Assert.NotEmpty(JsonSerializer.Deserialize<VersionEntry[]>(owner.Json!)!);

        IpcResponse stranger = _host.Handle(new IpcRequest { Command = IpcCommand.GetHistory, Arg1 = "Docs/private.txt" }, Stranger);
        Assert.False(stranger.Ok);
        Assert.Contains("access", stranger.Error!, StringComparison.OrdinalIgnoreCase);

        // An administrator may see everything.
        Assert.True(_host.Handle(new IpcRequest { Command = IpcCommand.GetHistory, Arg1 = "Docs/private.txt" }, Admin).Ok);
    }

    [Fact]
    public async Task A_stranger_cannot_read_version_content_or_browse_another_users_files()
    {
        await CaptureAFile();

        IpcResponse read = _host.Handle(new IpcRequest { Command = IpcCommand.GetVersionContent, Arg1 = "latest:Docs/private.txt" }, Stranger);
        Assert.False(read.Ok);

        // Browse/search must not even reveal the file exists.
        IpcResponse browse = _host.Handle(new IpcRequest { Command = IpcCommand.BrowseVersions, Arg1 = "", Arg2 = "private", Limit = 500 }, Stranger);
        Assert.True(browse.Ok); // the call succeeds…
        Assert.Empty(JsonSerializer.Deserialize<BrowseEntry[]>(browse.Json!)!); // …but shows the stranger nothing

        // The owner sees it.
        IpcResponse mine = _host.Handle(new IpcRequest { Command = IpcCommand.BrowseVersions, Arg1 = "", Arg2 = "private", Limit = 500 }, Owner);
        Assert.NotEmpty(JsonSerializer.Deserialize<BrowseEntry[]>(mine.Json!)!);
    }

    [Fact]
    public async Task A_stranger_cannot_restore_another_users_version()
    {
        await CaptureAFile();
        VersionEntry[] versions = JsonSerializer.Deserialize<VersionEntry[]>(
            _host.Handle(new IpcRequest { Command = IpcCommand.GetHistory, Arg1 = "Docs/private.txt" }, Owner).Json!)!;

        IpcResponse restore = _host.Handle(new IpcRequest
        {
            Command = IpcCommand.RestoreVersion,
            Arg1 = versions[0].VersionId,
            Arg2 = @"C:\Windows\Temp\stolen.txt", // and the destinationOverride must be ignored
        }, Stranger);

        Assert.False(restore.Ok);
    }

    [Fact]
    public async Task A_stranger_cannot_purge_another_users_history_and_cannot_wipe_everything()
    {
        await CaptureAFile();

        // Targeted purge of a root the stranger doesn't own → denied.
        Assert.False(_host.Handle(new IpcRequest { Command = IpcCommand.PurgeHistory, Arg1 = "Docs" }, Stranger).Ok);
        // Machine-wide wipe → admin only.
        Assert.False(_host.Handle(new IpcRequest { Command = IpcCommand.PurgeHistory, Arg1 = "*" }, Stranger).Ok);
        Assert.False(_host.Handle(new IpcRequest { Command = IpcCommand.PurgeHistory, Arg1 = "unprotected" }, Stranger).Ok);

        // History is intact after the denied attempts.
        Assert.NotEmpty(JsonSerializer.Deserialize<VersionEntry[]>(
            _host.Handle(new IpcRequest { Command = IpcCommand.GetHistory, Arg1 = "Docs/private.txt" }, Owner).Json!)!);

        // The owner can purge their own root.
        Assert.True(_host.Handle(new IpcRequest { Command = IpcCommand.PurgeHistory, Arg1 = "Docs" }, Owner).Ok);
    }

    [Fact]
    public void A_forged_operation_handle_is_rejected_not_executed()
    {
        // The pre-fix code deserialized a caller-supplied FileOperation and moved files as SYSTEM.
        // Now the handle is an opaque ring lookup: an arbitrary/forged handle matches nothing.
        string forged = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new
        {
            Kind = "Move",
            OldPath = @"C:\Windows\System32\evil.dll",
            NewPath = @"C:\Users\victim\Documents\report.docx",
        }));

        IpcResponse resp = _host.Handle(new IpcRequest { Command = IpcCommand.ReverseOperation, Arg1 = forged }, Stranger);
        Assert.False(resp.Ok);
        Assert.Contains("not found", resp.Error!, StringComparison.OrdinalIgnoreCase);

        // A syntactically bogus handle is likewise rejected, never acted upon.
        Assert.False(_host.Handle(new IpcRequest { Command = IpcCommand.ReverseOperation, Arg1 = "not-a-real-handle" }, Admin).Ok);
    }

    [Fact]
    public void Adding_a_folder_that_collides_on_leaf_name_is_refused()
    {
        // Two protected folders sharing a leaf would merge different files under one history —
        // the collision the audit flagged as a wrong-tree restore/purge hazard. Refuse it.
        string otherDocs = Path.Combine(_root, "other", "Docs");
        Directory.CreateDirectory(otherDocs);

        IpcResponse resp = _host.Handle(new IpcRequest
        {
            Command = IpcCommand.SetSettings,
            Arg1 = JsonSerializer.Serialize(new { WatchedFolders = new[] { _watch, otherDocs } }),
        }, Admin);

        Assert.False(resp.Ok);
        Assert.Contains("same name", resp.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_in_process_caller_keeps_full_trust()
    {
        // Tests, the CLI, and direct in-process calls run inside the engine's trust boundary and
        // must not be gated — the no-arg Handle overload maps to LocalTrusted.
        Assert.True(_host.Handle(new IpcRequest { Command = IpcCommand.GetStatus }).Ok);
        Assert.True(_host.Handle(new IpcRequest { Command = IpcCommand.PurgeHistory, Arg1 = "*" }).Ok);
    }

    public void Dispose()
    {
        _host.Dispose();
        try { Directory.Delete(_root, true); } catch { }
    }
}
