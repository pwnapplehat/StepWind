using System.Text.Json;
using StepWind.Core.Engine;
using StepWind.Core.Ipc;
using StepWind.Core.Storage;
using Xunit;

namespace StepWind.Core.Tests;

/// <summary>
/// Stable per-root namespaces: two protected folders may share a leaf name ("Documents") and keep
/// fully separate histories. The rules pinned here:
///  • existing roots keep their leaf namespace (zero migration on upgrade);
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

        // Same file name in both folders, different content — the old model would have refused
        // the second folder outright; now each lives in its own namespace.
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
    public void Existing_roots_keep_their_leaf_namespace_on_upgrade()
    {
        string docs = Path.Combine(_root, "user", "Documents");
        Directory.CreateDirectory(docs);
        string storeRoot = Path.Combine(_root, "store");

        // Simulate a pre-RootIds install: store already has history under the plain leaf.
        var log = new VersionLog(Path.Combine(storeRoot, "versions.jsonl"));
        log.Append(new FileVersion { RelativePath = "Documents/old.txt", CapturedUtc = DateTime.UtcNow, Size = 1, Chunks = [] });

        StepWindSettings settings = Settings(storeRoot, docs); // RootIds empty = upgrade
        using var host = new StepWindHost(settings, new GzipBlobCodec());

        // The folder adopted its own historical namespace — no migration, no suffix.
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

        // The upgrade path is the opposite by design: a folder already watched at startup CLAIMS
        // its own leaf segment (see Existing_roots_keep_their_leaf_namespace_on_upgrade).
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
