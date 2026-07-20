using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StepWind.Core.Engine;
using StepWind.Core.Ipc;
using StepWind.Core.Storage;
using Xunit;

namespace StepWind.Core.Tests;

/// <summary>
/// The encryption toggle: a store must stay fully readable while (and after) its blobs are
/// re-encoded between plain and encrypted formats, in both directions, across interruptions.
/// </summary>
public class CodecMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "stepwind-mig", Guid.NewGuid().ToString("N"));
    private readonly byte[] _key = SHA256.HashData("test-key-material"u8.ToArray());

    private MigratingBlobCodec NewCodec(bool encrypt)
        => new(new GzipBlobCodec(), () => new AesGcmBlobCodec(_key), encrypt);

    private string StorePath => Path.Combine(_root, "store");

    private VersionStore NewStore(IBlobCodec codec)
        => new(new BlobStore(StorePath, codec), new VersionLog(Path.Combine(StorePath, "versions.jsonl")));

    private string WriteSource(string name, string content)
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        string p = Path.Combine(_root, "src", name);
        File.WriteAllText(p, content);
        return p;
    }

    private static string ReadBack(VersionStore store, FileVersion v)
    {
        using var ms = new MemoryStream();
        store.WriteContent(v, ms);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    [Fact]
    public void Plain_history_stays_readable_after_enabling_encryption()
    {
        // History written before encryption existed…
        VersionStore plain = NewStore(NewCodec(encrypt: false));
        FileVersion v1 = plain.Capture(WriteSource("a.txt", "written before encryption"), "src/a.txt");

        // …must read fine through an encrypting store, before any re-encode has run.
        VersionStore enc = NewStore(NewCodec(encrypt: true));
        Assert.Equal("written before encryption", ReadBack(enc, v1));
    }

    [Fact]
    public void ReEncodeAll_encrypts_every_blob_and_content_survives()
    {
        VersionStore plain = NewStore(NewCodec(encrypt: false));
        var versions = new List<FileVersion>();
        for (int i = 0; i < 8; i++)
        {
            versions.Add(plain.Capture(WriteSource($"f{i}.txt", $"secret payload number {i}"), $"src/f{i}.txt"));
        }

        MigratingBlobCodec codec = NewCodec(encrypt: true);
        VersionStore enc = NewStore(codec);
        int converted = enc.Blobs.ReEncodeAll();
        Assert.True(converted >= 8);

        // No blob on disk may contain the plaintext anymore…
        foreach (string blob in Directory.EnumerateFiles(Path.Combine(StorePath, "blobs"), "*", SearchOption.AllDirectories))
        {
            Assert.DoesNotContain("secret payload", File.ReadAllText(blob));
        }

        // …and every version still restores byte-exact.
        for (int i = 0; i < versions.Count; i++)
        {
            Assert.Equal($"secret payload number {i}", ReadBack(enc, versions[i]));
        }

        // A second pass is a no-op (idempotent).
        Assert.Equal(0, enc.Blobs.ReEncodeAll());
    }

    [Fact]
    public void ReEncodeAll_decrypts_back_when_encryption_is_turned_off()
    {
        VersionStore enc = NewStore(NewCodec(encrypt: true));
        FileVersion v = enc.Capture(WriteSource("s.txt", "round trip me"), "src/s.txt");

        VersionStore plain = NewStore(NewCodec(encrypt: false));
        Assert.True(plain.Blobs.ReEncodeAll() >= 1);

        // Now readable by a codec with NO cipher at all — proof it's genuinely plain again.
        VersionStore gzipOnly = NewStore(new GzipBlobCodec());
        Assert.Equal("round trip me", ReadBack(gzipOnly, v));
    }

    [Fact]
    public void Interrupted_migration_leaves_a_fully_readable_mixed_store()
    {
        VersionStore plain = NewStore(NewCodec(encrypt: false));
        var versions = new List<FileVersion>();
        for (int i = 0; i < 6; i++)
        {
            versions.Add(plain.Capture(WriteSource($"m{i}.txt", $"mixed content {i}"), $"src/m{i}.txt"));
        }

        // Cancel after the first few blobs — simulates a service stop / crash mid-pass.
        MigratingBlobCodec codec = NewCodec(encrypt: true);
        VersionStore enc = NewStore(codec);
        using var cts = new CancellationTokenSource();
        int seen = 0;
        try
        {
            enc.Blobs.ReEncodeAll(cts.Token, progress: (done, _) =>
            {
                if (++seen == 3)
                {
                    cts.Cancel();
                }
            });
        }
        catch (OperationCanceledException)
        {
            // expected
        }

        // The store now holds a mix of gzip and AES blobs — every version must still read.
        for (int i = 0; i < versions.Count; i++)
        {
            Assert.Equal($"mixed content {i}", ReadBack(enc, versions[i]));
        }

        // Resuming converges the remainder.
        enc.Blobs.ReEncodeAll();
        Assert.Equal(0, enc.Blobs.ReEncodeAll());
    }

    [Fact]
    public void TotalBytes_tracks_the_actual_disk_usage()
    {
        MigratingBlobCodec codec = NewCodec(encrypt: false);
        VersionStore store = NewStore(codec);
        for (int i = 0; i < 5; i++)
        {
            store.Capture(WriteSource($"b{i}.bin", new string((char)('a' + i), 4000 + i * 500)), $"src/b{i}.bin");
        }

        long onDisk = Directory.EnumerateFiles(Path.Combine(StorePath, "blobs"), "*", SearchOption.AllDirectories)
            .Sum(f => new FileInfo(f).Length);
        Assert.Equal(onDisk, store.Blobs.TotalBytes);

        // Survives a re-encode (sizes change) and a reopen (startup scan).
        store.Blobs.ReEncodeAll();
        long afterReEncode = Directory.EnumerateFiles(Path.Combine(StorePath, "blobs"), "*", SearchOption.AllDirectories)
            .Sum(f => new FileInfo(f).Length);
        Assert.Equal(afterReEncode, store.Blobs.TotalBytes);

        var reopened = new BlobStore(StorePath, codec);
        Assert.Equal(afterReEncode, reopened.TotalBytes);
    }

    [Fact]
    public async Task Host_toggles_encryption_over_ipc_and_reports_status()
    {
        string watch = Path.Combine(_root, "watch");
        Directory.CreateDirectory(watch);
        var settings = new StepWindSettings
        {
            WatchedFolders = [watch],
            StoreRoot = StorePath,
            FlightRecorderEnabled = false,
            EncryptionEnabled = false,
        };

        using var host = new StepWindHost(settings, NewCodec(encrypt: false));

        // Capture one version so there's something to re-encode.
        File.WriteAllText(Path.Combine(watch, "doc.txt"), "host toggle content");
        for (int i = 0; i < 40; i++)
        {
            IpcResponse h = host.Handle(new IpcRequest { Command = IpcCommand.GetHistory, Arg1 = "watch/doc.txt" });
            if (h.Ok && (JsonSerializer.Deserialize<VersionEntry[]>(h.Json!)?.Length ?? 0) > 0)
            {
                break;
            }

            await Task.Delay(250);
        }

        // Toggle encryption on via the same patch the GUI sends.
        IpcResponse resp = host.Handle(new IpcRequest
        {
            Command = IpcCommand.SetSettings,
            Arg1 = JsonSerializer.Serialize(new { EncryptionEnabled = true }),
        });
        Assert.True(resp.Ok);
        Assert.Contains("\"EncryptionEnabled\":true", resp.Json);

        // Status reports storage bytes; re-encode finishes and the marker goes clean.
        for (int i = 0; i < 40; i++)
        {
            IpcResponse st = host.Handle(new IpcRequest { Command = IpcCommand.GetStatus });
            using JsonDocument doc = JsonDocument.Parse(st.Json!);
            if (!doc.RootElement.GetProperty("ReEncoding").GetBoolean())
            {
                Assert.True(doc.RootElement.GetProperty("StoreBytes").GetInt64() > 0);
                break;
            }

            await Task.Delay(250);
        }

        Assert.Equal("cipher:clean", File.ReadAllText(Path.Combine(StorePath, "codec.state")));
        Assert.DoesNotContain("host toggle content",
            Directory.EnumerateFiles(Path.Combine(StorePath, "blobs"), "*", SearchOption.AllDirectories)
                .Select(File.ReadAllText).SelectMany(s => new[] { s }).Aggregate("", (a, b) => a + b));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
