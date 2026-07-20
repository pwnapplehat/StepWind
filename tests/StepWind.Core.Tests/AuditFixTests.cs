using System.Runtime.Versioning;
using StepWind.Core.Engine;
using StepWind.Core.Storage;
using Xunit;

namespace StepWind.Core.Tests;

/// <summary>Regression tests for the world-class audit pass: dedup, reconcile, GC race, DPAPI key.</summary>
public class AuditFixTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "stepwind-audit", Guid.NewGuid().ToString("N"));
    private readonly string _watch;

    public AuditFixTests()
    {
        _watch = Path.Combine(_root, "Docs");
        Directory.CreateDirectory(_watch);
    }

    private VersionStore NewStore(IBlobCodec? codec = null)
        => new(new BlobStore(Path.Combine(_root, "store"), codec ?? new GzipBlobCodec()),
               new VersionLog(Path.Combine(_root, "store", "versions.jsonl")));

    [Fact]
    public void Identical_content_does_not_create_a_second_version()
    {
        VersionStore store = NewStore();
        string file = Path.Combine(_watch, "a.txt");
        File.WriteAllText(file, "same bytes");

        store.Capture(file, "Docs/a.txt");
        // A watcher can fire again on a no-op touch; capturing again must be a no-op.
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(1));
        store.Capture(file, "Docs/a.txt");

        Assert.Single(store.Log.History("Docs/a.txt"));
    }

    [Fact]
    public void Changed_content_does_create_a_new_version()
    {
        VersionStore store = NewStore();
        string file = Path.Combine(_watch, "a.txt");
        File.WriteAllText(file, "one"); store.Capture(file, "Docs/a.txt");
        File.WriteAllText(file, "two"); store.Capture(file, "Docs/a.txt");
        Assert.Equal(2, store.Log.History("Docs/a.txt").Count);
    }

    [Fact]
    public void Reconcile_captures_files_that_changed_while_stopped()
    {
        VersionStore store = NewStore();
        // Files already on disk before StepWind ever watched this folder (the "was down" case).
        File.WriteAllText(Path.Combine(_watch, "pre1.txt"), "existing 1");
        File.WriteAllText(Path.Combine(_watch, "pre2.txt"), "existing 2");
        Directory.CreateDirectory(Path.Combine(_watch, "sub"));
        File.WriteAllText(Path.Combine(_watch, "sub", "pre3.txt"), "existing 3");

        using var engine = new WatchEngine(store, new PathExclusions(), [_watch]);
        int captured = engine.Reconcile();

        Assert.Equal(3, captured);
        Assert.Single(store.Log.History("Docs/pre1.txt"));
        Assert.Single(store.Log.History("Docs/sub/pre3.txt"));
    }

    [Fact]
    public void Reconcile_is_idempotent_and_skips_unchanged_files()
    {
        VersionStore store = NewStore();
        File.WriteAllText(Path.Combine(_watch, "x.txt"), "data");

        using var engine = new WatchEngine(store, new PathExclusions(), [_watch]);
        Assert.Equal(1, engine.Reconcile());
        Assert.Equal(0, engine.Reconcile()); // nothing changed → nothing re-captured
        Assert.Single(store.Log.History("Docs/x.txt"));
    }

    [Fact]
    public void Reconcile_captures_a_newer_edit_after_a_prior_version_exists()
    {
        VersionStore store = NewStore();
        string file = Path.Combine(_watch, "x.txt");
        File.WriteAllText(file, "v1");
        using var engine = new WatchEngine(store, new PathExclusions(), [_watch]);
        engine.Reconcile();

        // Simulate an edit that happened while StepWind was NOT running.
        File.WriteAllText(file, "v2-edited-while-down");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(5));

        Assert.Equal(1, engine.Reconcile());
        Assert.Equal(2, store.Log.History("Docs/x.txt").Count);
    }

    [Fact]
    public void Gc_under_the_maintenance_gate_keeps_a_concurrently_captured_versions_chunks()
    {
        // Interleave a capture with a retention/GC pass and assert every kept version's chunks
        // still resolve — i.e. the gate prevents sweeping an in-flight blob.
        VersionStore store = NewStore();
        for (int i = 0; i < 20; i++)
        {
            File.WriteAllText(Path.Combine(_watch, $"f{i}.txt"), $"content number {i} " + new string('x', i * 100));
            store.Capture(Path.Combine(_watch, $"f{i}.txt"), $"Docs/f{i}.txt");
        }

        var captureThread = new Thread(() =>
        {
            for (int i = 0; i < 50; i++)
            {
                string f = Path.Combine(_watch, $"live{i}.txt");
                File.WriteAllText(f, $"live {i} " + new string('y', i * 50));
                store.Capture(f, $"Docs/live{i}.txt");
            }
        });
        captureThread.Start();
        for (int r = 0; r < 5; r++)
        {
            store.RunExclusive(() => Retention.Apply(store.Log, store.Blobs, new RetentionPolicy(), DateTime.UtcNow));
        }

        captureThread.Join();
        store.RunExclusive(() => Retention.Apply(store.Log, store.Blobs, new RetentionPolicy(), DateTime.UtcNow));

        // Every surviving version must be fully restorable (all chunks present + verified).
        foreach (FileVersion v in store.Log.All)
        {
            using var ms = new MemoryStream();
            Exception? ex = Record.Exception(() => store.WriteContent(v, ms));
            Assert.Null(ex);
            Assert.Equal(v.Size, ms.Length);
        }
    }

    [SupportedOSPlatform("windows")]
    [Fact]
    public void Dpapi_key_is_stable_and_encrypts_the_store()
    {
        string storeRoot = Path.Combine(_root, "encstore");
        byte[] key1 = KeyProtector.LoadOrCreate(storeRoot);
        byte[] key2 = KeyProtector.LoadOrCreate(storeRoot); // second call returns the same key
        Assert.Equal(key1, key2);
        Assert.Equal(32, key1.Length);

        var enc = new VersionStore(new BlobStore(storeRoot, new AesGcmBlobCodec(key1)),
            new VersionLog(Path.Combine(storeRoot, "versions.jsonl")));
        string file = Path.Combine(_watch, "secret.txt");
        File.WriteAllText(file, "confidential content");
        FileVersion v = enc.Capture(file, "Docs/secret.txt");

        // The plaintext must NOT appear in any on-disk blob.
        foreach (string blob in Directory.EnumerateFiles(Path.Combine(storeRoot, "blobs"), "*", SearchOption.AllDirectories))
        {
            string raw = File.ReadAllText(blob);
            Assert.DoesNotContain("confidential", raw);
        }

        // But a store reopened with the reloaded key restores it byte-exact.
        var reopened = new VersionStore(new BlobStore(storeRoot, new AesGcmBlobCodec(key2)),
            new VersionLog(Path.Combine(storeRoot, "versions.jsonl")));
        using var ms = new MemoryStream();
        reopened.WriteContent(reopened.Log.History("Docs/secret.txt")[0], ms);
        Assert.Equal("confidential content", System.Text.Encoding.UTF8.GetString(ms.ToArray()));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
