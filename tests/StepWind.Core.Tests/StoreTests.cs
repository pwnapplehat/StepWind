using System.Text;
using StepWind.Core.Storage;
using Xunit;

namespace StepWind.Core.Tests;

public class StoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "stepwind-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Put_is_content_addressed_and_deduplicates()
    {
        var store = new BlobStore(_root, new GzipBlobCodec());
        byte[] data = Encoding.UTF8.GetBytes("the quick brown fox");

        (BlobId id1, bool w1) = store.Put(data);
        (BlobId id2, bool w2) = store.Put(data);

        Assert.Equal(id1, id2);
        Assert.True(w1);
        Assert.False(w2); // second put dedups, writes nothing
        Assert.Equal(data, store.Get(id1));
    }

    [Fact]
    public void Get_verifies_integrity_and_throws_on_corruption()
    {
        var store = new BlobStore(_root, new GzipBlobCodec());
        (BlobId id, _) = store.Put(Encoding.UTF8.GetBytes("payload"));

        // Corrupt the on-disk blob, then read: must be caught, not returned as garbage.
        string blobPath = Path.Combine(_root, "blobs", id.RelativePath);
        File.WriteAllBytes(blobPath, [.. Enumerable.Repeat((byte)0, 200)]);

        Assert.ThrowsAny<Exception>(() => store.Get(id));
    }

    [Fact]
    public void Gzip_and_aesgcm_produce_the_same_blob_id_for_the_same_plaintext()
    {
        // Blob id hashes PLAINTEXT, so dedup/lookups survive turning encryption on.
        byte[] key = new byte[32];
        new Random(3).NextBytes(key);
        byte[] data = Encoding.UTF8.GetBytes("switch-codec-safe");

        (BlobId plainId, _) = new BlobStore(Path.Combine(_root, "a"), new GzipBlobCodec()).Put(data);
        (BlobId encId, _) = new BlobStore(Path.Combine(_root, "b"), new AesGcmBlobCodec(key)).Put(data);

        Assert.Equal(plainId, encId);
    }

    [Fact]
    public void Aesgcm_round_trips_and_rejects_tampering()
    {
        byte[] key = new byte[32];
        new Random(5).NextBytes(key);
        var store = new BlobStore(_root, new AesGcmBlobCodec(key));
        byte[] secret = Encoding.UTF8.GetBytes("confidential version content");

        (BlobId id, _) = store.Put(secret);
        Assert.Equal(secret, store.Get(id));

        // Flip a ciphertext byte → GCM auth tag fails → throws (no silent bad restore).
        string blobPath = Path.Combine(_root, "blobs", id.RelativePath);
        byte[] onDisk = File.ReadAllBytes(blobPath);
        onDisk[^1] ^= 0xFF;
        File.WriteAllBytes(blobPath, onDisk);
        Assert.ThrowsAny<Exception>(() => store.Get(id));
    }

    [Fact]
    public void Wrong_passphrase_cannot_read_blobs()
    {
        byte[] salt = new byte[16];
        new Random(9).NextBytes(salt);
        byte[] good = AesGcmBlobCodec.DeriveKey("correct horse", salt);
        byte[] bad = AesGcmBlobCodec.DeriveKey("wrong horse", salt);

        var writeStore = new BlobStore(_root, new AesGcmBlobCodec(good));
        (BlobId id, _) = writeStore.Put(Encoding.UTF8.GetBytes("locked"));

        var readStore = new BlobStore(_root, new AesGcmBlobCodec(bad));
        Assert.ThrowsAny<Exception>(() => readStore.Get(id));
    }

    [Fact]
    public void CleanTemp_removes_orphaned_interrupted_writes()
    {
        var store = new BlobStore(_root, new GzipBlobCodec());
        string tempDir = Path.Combine(_root, "tmp");
        File.WriteAllText(Path.Combine(tempDir, "crashed.tmp"), "half-written");

        Assert.Equal(1, store.CleanTemp());
        Assert.Empty(Directory.GetFiles(tempDir));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
