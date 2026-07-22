using System.Security.Cryptography;
using StepWind.Core.Storage;
using Xunit;

namespace StepWind.Core.Tests;

/// <summary>
/// P1-6: encryption at rest must not become a way to LOSE data. The live key is machine-DPAPI
/// sealed, so an OS reinstall or disk move would orphan encrypted history forever. A
/// passphrase-protected recovery key is the escape hatch — these pin that it round-trips exactly,
/// rejects a wrong passphrase, and actually unlocks a real encrypted store.
/// </summary>
public class KeyRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sw-recovery", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Export_then_import_recovers_the_exact_key()
    {
        byte[] key = RandomNumberGenerator.GetBytes(32);
        byte[] blob = KeyRecovery.Export(key, "correct horse battery staple", iterations: 50_000);

        byte[] recovered = KeyRecovery.Import(blob, "correct horse battery staple");
        Assert.Equal(key, recovered);
    }

    [Fact]
    public void A_wrong_passphrase_fails_cleanly()
    {
        byte[] key = RandomNumberGenerator.GetBytes(32);
        byte[] blob = KeyRecovery.Export(key, "the-real-passphrase", iterations: 50_000);

        // AES-GCM auth failure surfaces as AuthenticationTagMismatchException (a CryptographicException).
        Assert.ThrowsAny<CryptographicException>(() => KeyRecovery.Import(blob, "not-the-passphrase"));
    }

    [Fact]
    public void A_tampered_or_foreign_blob_is_rejected()
    {
        Assert.ThrowsAny<CryptographicException>(() => KeyRecovery.Import(new byte[10], "x"));

        byte[] blob = KeyRecovery.Export(RandomNumberGenerator.GetBytes(32), "pw", iterations: 50_000);
        blob[^1] ^= 0xFF; // flip a tag bit
        Assert.ThrowsAny<CryptographicException>(() => KeyRecovery.Import(blob, "pw"));
    }

    [Fact]
    public void A_recovered_key_reads_a_store_encrypted_with_the_original()
    {
        // Simulate disaster recovery: a store was written encrypted with key K on the old machine;
        // on a new machine only the recovery file + passphrase exist. Import(K') must equal K and
        // decrypt the actual blobs.
        string storeRoot = Path.Combine(_root, "store");
        string watch = Path.Combine(_root, "Docs");
        Directory.CreateDirectory(watch);

        byte[] key = RandomNumberGenerator.GetBytes(32);
        var original = new VersionStore(
            new BlobStore(storeRoot, new AesGcmBlobCodec(key)),
            new VersionLog(Path.Combine(storeRoot, "versions.jsonl")));
        string file = Path.Combine(watch, "secret.txt");
        File.WriteAllText(file, "the encrypted truth");
        FileVersion v = original.Capture(file, "Docs/secret.txt");

        // Export the key, then recover it fresh (as if on a new machine).
        byte[] blob = KeyRecovery.Export(key, "recovery-pass", iterations: 50_000);
        byte[] recovered = KeyRecovery.Import(blob, "recovery-pass");

        var recoveredStore = new VersionStore(
            new BlobStore(storeRoot, new AesGcmBlobCodec(recovered)),
            new VersionLog(Path.Combine(storeRoot, "versions.jsonl")));
        using var ms = new MemoryStream();
        recoveredStore.WriteContent(recoveredStore.Log.History("Docs/secret.txt")[0], ms);
        Assert.Equal("the encrypted truth", System.Text.Encoding.UTF8.GetString(ms.ToArray()));
        Assert.Equal(v.Size, ms.Length);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
