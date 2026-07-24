namespace StepWind.Core.Storage;

/// <summary>Outcome of a retention + GC pass, for logging and the UI.</summary>
public sealed record RetentionResult(int VersionsBefore, int VersionsKept, int BlobsSwept);

/// <summary>
/// Applies a <see cref="RetentionPolicy"/> and then garbage-collects the blob store.
///
/// GC is mark-and-sweep and MUST run only after the log has been rewritten to the survivors:
/// mark every chunk referenced by a surviving version, then sweep (delete) any blob not
/// marked. This is the only safe order — a blob is removed only once nothing references it —
/// so the store never loses a chunk a live version still needs.
/// </summary>
public static class Retention
{
    public static RetentionResult Apply(VersionLog log, BlobStore blobs, RetentionPolicy policy, DateTime nowUtc)
    {
        IReadOnlyList<FileVersion> all = log.All;
        int before = all.Count;

        var keep = new List<FileVersion>(all.Count);
        foreach (IGrouping<string, FileVersion> group in all.GroupBy(v => v.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            keep.AddRange(SelectForFile([.. group.OrderByDescending(v => v.CapturedUtc)], policy, nowUtc));
        }

        if (keep.Count != before)
        {
            log.Rewrite([.. keep.OrderBy(v => v.CapturedUtc)]);
        }

        int swept = Sweep(log, blobs);
        return new RetentionResult(before, keep.Count, swept);
    }

    /// <summary>Chooses which versions of ONE file to keep (input newest-first).</summary>
    private static List<FileVersion> SelectForFile(List<FileVersion> newestFirst, RetentionPolicy policy, DateTime nowUtc)
    {
        var kept = new List<FileVersion>();
        DateTime allCutoff = nowUtc.AddHours(-policy.KeepAllHours);
        DateTime hourlyCutoff = nowUtc.AddDays(-policy.HourlyDays);
        DateTime dailyCutoff = nowUtc.AddDays(-policy.DailyDays);
        DateTime ageCutoff = nowUtc.AddDays(-policy.MaxAgeDays);

        long lastHourlyBucket = long.MinValue;
        long lastDailyBucket = long.MinValue;
        long lastWeeklyBucket = long.MinValue;

        foreach (FileVersion v in newestFirst)
        {
            if (v.CapturedUtc < ageCutoff)
            {
                break; // older than the hard cap — drop the rest
            }

            if (kept.Count >= policy.MaxVersionsPerFile)
            {
                break;
            }

            bool keepThis;
            if (v.CapturedUtc >= allCutoff)
            {
                keepThis = true; // recent window: keep everything
            }
            else if (v.CapturedUtc >= hourlyCutoff)
            {
                long bucket = v.CapturedUtc.Ticks / TimeSpan.TicksPerHour;
                keepThis = bucket != lastHourlyBucket;
                if (keepThis) lastHourlyBucket = bucket;
            }
            else if (v.CapturedUtc >= dailyCutoff)
            {
                long bucket = v.CapturedUtc.Ticks / TimeSpan.TicksPerDay;
                keepThis = bucket != lastDailyBucket;
                if (keepThis) lastDailyBucket = bucket;
            }
            else
            {
                // Weekly tier: one version per 7-day bucket. Must track its OWN last-bucket, not
                // reuse lastDailyBucket (a day-bucket number of a different magnitude), or the
                // first weekly-tier version is compared against a stale daily value.
                long bucket = v.CapturedUtc.Ticks / (TimeSpan.TicksPerDay * 7);
                keepThis = bucket != lastWeeklyBucket;
                if (keepThis) lastWeeklyBucket = bucket;
            }

            if (keepThis)
            {
                kept.Add(v);
            }
        }

        // Always keep the single newest version even if buckets would drop it.
        if (kept.Count == 0 && newestFirst.Count > 0)
        {
            kept.Add(newestFirst[0]);
        }

        return kept;
    }

    /// <summary>Mark referenced chunks from the (already-pruned) log, sweep the rest.</summary>
    public static int Sweep(VersionLog log, BlobStore blobs)
    {
        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (FileVersion v in log.All)
        {
            foreach (string chunk in v.Chunks)
            {
                live.Add(chunk);
            }
        }

        int swept = 0;
        foreach (BlobId id in blobs.EnumerateAll())
        {
            if (!live.Contains(id.Hex) && blobs.Delete(id))
            {
                swept++;
            }
        }

        return swept;
    }
}
