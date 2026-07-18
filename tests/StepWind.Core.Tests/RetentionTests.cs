using StepWind.Core.Storage;
using Xunit;

namespace StepWind.Core.Tests;

public class RetentionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "stepwind-ret", Guid.NewGuid().ToString("N"));

    private (VersionStore Store, VersionLog Log, BlobStore Blobs) New()
    {
        var blobs = new BlobStore(Path.Combine(_root, "s"), new GzipBlobCodec());
        var log = new VersionLog(Path.Combine(_root, "s", "versions.jsonl"));
        return (new VersionStore(blobs, log), log, blobs);
    }

    private static FileVersion Ver(VersionLog log, BlobStore blobs, string path, DateTime whenUtc, string content)
    {
        (BlobId id, _) = blobs.Put(System.Text.Encoding.UTF8.GetBytes(content));
        return log.Append(new FileVersion
        {
            RelativePath = path,
            CapturedUtc = whenUtc,
            ModifiedUtc = whenUtc,
            Size = content.Length,
            Chunks = [id.Hex],
        });
    }

    [Fact]
    public void Recent_versions_are_all_kept()
    {
        (_, VersionLog log, BlobStore blobs) = New();
        DateTime now = DateTime.UtcNow;
        for (int i = 0; i < 10; i++)
        {
            Ver(log, blobs, "a.txt", now.AddMinutes(-i * 30), "c" + i); // all within 24h
        }

        RetentionResult r = Retention.Apply(log, blobs, new RetentionPolicy(), now);
        Assert.Equal(10, r.VersionsKept);
    }

    [Fact]
    public void Old_versions_are_thinned_and_hard_age_capped()
    {
        (_, VersionLog log, BlobStore blobs) = New();
        DateTime now = DateTime.UtcNow;

        // 50 versions/day for 400 days — way past every tier and the 365-day hard cap.
        for (int day = 0; day < 400; day++)
        {
            for (int k = 0; k < 3; k++)
            {
                Ver(log, blobs, "a.txt", now.AddDays(-day).AddHours(-k), $"d{day}k{k}");
            }
        }

        var policy = new RetentionPolicy();
        RetentionResult r = Retention.Apply(log, blobs, policy, now);

        Assert.True(r.VersionsKept < r.VersionsBefore, "must thin");
        Assert.True(r.VersionsKept <= policy.MaxVersionsPerFile, "must honor per-file cap");
        // Nothing older than the hard age cap survives.
        Assert.DoesNotContain(log.All, v => v.CapturedUtc < now.AddDays(-policy.MaxAgeDays));
    }

    [Fact]
    public void Gc_sweeps_unreferenced_blobs_but_keeps_live_ones()
    {
        (_, VersionLog log, BlobStore blobs) = New();
        DateTime now = DateTime.UtcNow;

        FileVersion keep = Ver(log, blobs, "a.txt", now, "still referenced");
        // An orphan blob not referenced by any version.
        (BlobId orphan, _) = blobs.Put(System.Text.Encoding.UTF8.GetBytes("orphaned garbage"));

        int swept = Retention.Sweep(log, blobs);

        Assert.Equal(1, swept);
        Assert.False(blobs.Exists(orphan));
        foreach (string live in keep.Chunks)
        {
            Assert.True(blobs.Exists(BlobId.Parse(live)), "a referenced chunk must never be swept");
        }
    }

    [Fact]
    public void Newest_version_is_always_kept_even_if_buckets_would_drop_it()
    {
        (_, VersionLog log, BlobStore blobs) = New();
        DateTime now = DateTime.UtcNow;
        FileVersion only = Ver(log, blobs, "a.txt", now.AddDays(-300), "ancient but only copy");

        Retention.Apply(log, blobs, new RetentionPolicy(), now);
        Assert.Contains(log.All, v => v.CapturedUtc == only.CapturedUtc);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
