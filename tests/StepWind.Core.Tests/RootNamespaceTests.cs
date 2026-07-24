using System.Text.Json;
using StepWind.Core.Engine;
using StepWind.Core.Ipc;
using StepWind.Core.Storage;
using Xunit;

namespace StepWind.Core.Tests;

/// <summary>
/// Stable per-root namespaces: two protected folders may share a leaf name ("Documents") and keep
/// fully separate histories. The rules pinned here:
///  • a folder already watched at startup adopts its own unmapped history (settings-reset safety);
///  • a colliding new root gets a deterministic "leaf~hash" namespace;
///  • dead history in the store also blocks a leaf (a new unrelated folder must never silently
///    adopt a removed folder's timeline);
///  • re-adding the SAME path reuses its namespace, so its old history re-attaches.
/// </summary>
public class RootNamespaceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sw-ns", Guid.NewGuid().ToString("N"));

    private static StepWindSettings Settings(string store, params string[] folders) => new()
    {
        StoreRoot = store,
        WatchedFolders = [.. folders],
        FlightRecorderEnabled = false,
    };

    private static string CaptureNow(StepWindHost host, string absolutePath)
    {
        IpcResponse r = host.Handle(new IpcRequest { Command = IpcCommand.CaptureNow, Arg1 = absolutePath });
        Assert.True(r.Ok, r.Error);
        return JsonSerializer.Deserialize<VersionEntry>(r.Json!)!.RelativePath;
    }

    [Fact]
    public void Two_same_named_folders_are_both_protected_with_separate_histories()
    {
        string docsA = Path.Combine(_root, "driveA", "Documents");
        string docsB = Path.Combine(_root, "driveB", "Documents");
        Directory.CreateDirectory(docsA);
        Directory.CreateDirectory(docsB);

        StepWindSettings settings = Settings(Path.Combine(_root, "store"), docsA, docsB);
        using var host = new StepWindHost(settings, new GzipBlobCodec());

        // Same file name in both folders, different content — each folder lives in its own
        // namespace, so both are protected side by side and nothing merges.
        File.WriteAllText(Path.Combine(docsA, "notes.txt"), "contents from drive A");
        File.WriteAllText(Path.Combine(docsB, "notes.txt"), "contents from drive B");

        string relA = CaptureNow(host, Path.Combine(docsA, "notes.txt"));
        string relB = CaptureNow(host, Path.Combine(docsB, "notes.txt"));

        Assert.NotEqual(relA, relB); // distinct namespaces → distinct store paths
        Assert.StartsWith("Documents", relA.Split('/')[0]);
        Assert.StartsWith("Documents", relB.Split('/')[0]);

        // Each history holds exactly its own file's content, byte-exact.
        var store = new VersionStore(
            new BlobStore(settings.StoreRoot, new GzipBlobCodec()),
            new VersionLog(Path.Combine(settings.StoreRoot, "versions.jsonl")));
        using var msA = new MemoryStream();
        store.WriteContent(store.Log.History(relA)[^1], msA);
        Assert.Equal("contents from drive A", System.Text.Encoding.UTF8.GetString(msA.ToArray()));
        using var msB = new MemoryStream();
        store.WriteContent(store.Log.History(relB)[^1], msB);
        Assert.Equal("contents from drive B", System.Text.Encoding.UTF8.GetString(msB.ToArray()));
    }

    [Fact]
    public void Purge_unprotected_keeps_a_still_protected_folder_with_a_suffixed_namespace()
    {
        // C1 regression: "clean up unprotected history" must key off each root's STORE NAMESPACE,
        // not its leaf name. A second same-named folder gets a suffixed namespace ("Documents~..");
        // both folders are protected, so purge-unprotected must keep BOTH histories.
        string docsA = Path.Combine(_root, "driveA", "Documents");
        string docsB = Path.Combine(_root, "driveB", "Documents");
        Directory.CreateDirectory(docsA);
        Directory.CreateDirectory(docsB);

        StepWindSettings settings = Settings(Path.Combine(_root, "store"), docsA, docsB);
        using var host = new StepWindHost(settings, new GzipBlobCodec());

        File.WriteAllText(Path.Combine(docsA, "a.txt"), "alpha");
        File.WriteAllText(Path.Combine(docsB, "b.txt"), "bravo");
        string relA = CaptureNow(host, Path.Combine(docsA, "a.txt"));
        string relB = CaptureNow(host, Path.Combine(docsB, "b.txt"));
        Assert.NotEqual(relA.Split('/')[0], relB.Split('/')[0]); // one namespace is suffixed

        // Purge unprotected history (admin/in-process trusted). Nothing is unprotected here.
        IpcResponse purge = host.Handle(new IpcRequest { Command = IpcCommand.PurgeHistory, Arg1 = "unprotected" });
        Assert.True(purge.Ok, purge.Error);

        // Both protected folders' histories survive — the suffixed one was NOT mistaken for unprotected.
        Assert.NotEmpty(JsonSerializer.Deserialize<VersionEntry[]>(
            host.Handle(new IpcRequest { Command = IpcCommand.GetHistory, Arg1 = relA }).Json!)!);
        Assert.NotEmpty(JsonSerializer.Deserialize<VersionEntry[]>(
            host.Handle(new IpcRequest { Command = IpcCommand.GetHistory, Arg1 = relB }).Json!)!);
    }

    [Fact]
    public void Purge_unprotected_still_removes_a_removed_folders_history()
    {
        // The other half of C1: history whose namespace belongs to NO current watched folder is
        // genuinely unprotected and must still be swept.
        string docs = Path.Combine(_root, "user", "Documents");
        Directory.CreateDirectory(docs);
        StepWindSettings settings = Settings(Path.Combine(_root, "store"), docs);
        using var host = new StepWindHost(settings, new GzipBlobCodec());

        File.WriteAllText(Path.Combine(docs, "keep.txt"), "keep me");
        string relKept = CaptureNow(host, Path.Combine(docs, "keep.txt"));

        // Inject dead history under a namespace no watched folder maps to.
        settings.StoreRoot.ToString(); // (store already open) — append a stray version directly.
        var log = new VersionLog(Path.Combine(settings.StoreRoot, "versions.jsonl"));
        log.Append(new FileVersion { RelativePath = "OldProject/gone.cs", CapturedUtc = DateTime.UtcNow, Size = 1, Chunks = [] });

        using var host2 = new StepWindHost(settings, new GzipBlobCodec());
        IpcResponse purge = host2.Handle(new IpcRequest { Command = IpcCommand.PurgeHistory, Arg1 = "unprotected" });
        Assert.True(purge.Ok, purge.Error);

        Assert.NotEmpty(JsonSerializer.Deserialize<VersionEntry[]>(
            host2.Handle(new IpcRequest { Command = IpcCommand.GetHistory, Arg1 = relKept }).Json!)!); // protected kept
        Assert.Empty(JsonSerializer.Deserialize<VersionEntry[]>(
            host2.Handle(new IpcRequest { Command = IpcCommand.GetHistory, Arg1 = "OldProject/gone.cs" }).Json!)!); // dead swept
    }

    [Fact]
    public void A_watched_folder_adopts_its_own_unmapped_history_at_startup()
    {
        string docs = Path.Combine(_root, "user", "Documents");
        Directory.CreateDirectory(docs);
        string storeRoot = Path.Combine(_root, "store");

        // The store has history under the plain leaf but settings carry no mapping for it
        // (e.g. settings.json was reset or restored while the store survived).
        var log = new VersionLog(Path.Combine(storeRoot, "versions.jsonl"));
        log.Append(new FileVersion { RelativePath = "Documents/old.txt", CapturedUtc = DateTime.UtcNow, Size = 1, Chunks = [] });

        StepWindSettings settings = Settings(storeRoot, docs); // RootIds empty, store not
        using var host = new StepWindHost(settings, new GzipBlobCodec());

        // The folder adopted its own history's namespace — nothing orphaned, no suffix.
        Assert.Equal("Documents", settings.RootIds[docs]);
    }

    [Fact]
    public void Dead_history_blocks_the_leaf_so_an_unrelated_folder_never_adopts_it()
    {
        string storeRoot = Path.Combine(_root, "store");

        // A folder named "Projects" was protected once, captured history, then was removed —
        // only its dead store segment remains. A DIFFERENT folder that happens to be named
        // "Projects" is later added ONLINE (SetSettings, the way every post-install add works).
        var log = new VersionLog(Path.Combine(storeRoot, "versions.jsonl"));
        log.Append(new FileVersion { RelativePath = "Projects/legacy.cs", CapturedUtc = DateTime.UtcNow, Size = 1, Chunks = [] });

        string otherProjects = Path.Combine(_root, "elsewhere", "Projects");
        Directory.CreateDirectory(otherProjects);

        StepWindSettings settings = Settings(storeRoot); // starts with no watched folders
        using var host = new StepWindHost(settings, new GzipBlobCodec());
        IpcResponse r = host.Handle(new IpcRequest
        {
            Command = IpcCommand.SetSettings,
            Arg1 = JsonSerializer.Serialize(new { WatchedFolders = new[] { otherProjects } }),
        });
        Assert.True(r.Ok, r.Error);

        string ns = settings.RootIds[otherProjects];
        Assert.NotEqual("Projects", ns);                 // did NOT adopt the dead timeline
        Assert.StartsWith("Projects~", ns);              // deterministic suffixed id

        // The startup pass is the opposite by design: a folder already watched at startup CLAIMS
        // its own leaf segment (see A_watched_folder_adopts_its_own_unmapped_history_at_startup).
    }

    [Fact]
    public void Readding_the_same_path_reuses_its_namespace_and_reattaches_history()
    {
        string docs = Path.Combine(_root, "user", "Documents");
        Directory.CreateDirectory(docs);
        string storeRoot = Path.Combine(_root, "store");

        StepWindSettings settings = Settings(storeRoot, docs);
        string firstNs;
        using (var host = new StepWindHost(settings, new GzipBlobCodec()))
        {
            File.WriteAllText(Path.Combine(docs, "keep.txt"), "history to reattach");
            CaptureNow(host, Path.Combine(docs, "keep.txt"));
            firstNs = settings.RootIds[docs];

            // Remove the folder (history + RootIds mapping are kept by design).
            IpcResponse r = host.Handle(new IpcRequest
            {
                Command = IpcCommand.SetSettings,
                Arg1 = JsonSerializer.Serialize(new { WatchedFolders = Array.Empty<string>() }),
            });
            Assert.True(r.Ok, r.Error);
            Assert.True(settings.RootIds.ContainsKey(docs)); // mapping survives removal
        }

        // Re-add the same path in a fresh host: same namespace → old history is visible again.
        settings.WatchedFolders = [docs];
        using var host2 = new StepWindHost(settings, new GzipBlobCodec());
        Assert.Equal(firstNs, settings.RootIds[docs]);

        IpcResponse hist = host2.Handle(new IpcRequest { Command = IpcCommand.GetHistory, Arg1 = $"{firstNs}/keep.txt" });
        Assert.True(hist.Ok, hist.Error);
        Assert.NotEmpty(JsonSerializer.Deserialize<VersionEntry[]>(hist.Json!)!);
    }

    [Fact]
    public void Suffixed_namespace_is_deterministic_across_restarts()
    {
        string a = Path.Combine(_root, "one", "Data");
        string b = Path.Combine(_root, "two", "Data");
        Directory.CreateDirectory(a);
        Directory.CreateDirectory(b);
        string storeRoot = Path.Combine(_root, "store");

        StepWindSettings s1 = Settings(storeRoot, a, b);
        using (new StepWindHost(s1, new GzipBlobCodec())) { }
        string nsB1 = s1.RootIds[b];

        // A brand-new settings object (same paths, fresh store dir) computes the same suffix —
        // it's derived from the path, not from insertion order or randomness.
        StepWindSettings s2 = Settings(Path.Combine(_root, "store2"), a, b);
        using (new StepWindHost(s2, new GzipBlobCodec())) { }
        Assert.Equal(nsB1, s2.RootIds[b]);
        Assert.Equal("Data", s2.RootIds[a]); // first one keeps the plain leaf
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
