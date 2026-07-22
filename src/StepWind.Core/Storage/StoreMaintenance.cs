namespace StepWind.Core.Storage;

/// <summary>
/// The result of checking the history store's integrity: how many versions are fully restorable,
/// how many can't be (a chunk is missing or corrupt), and how many orphaned blobs are wasting
/// space. <see cref="RemovedVersions"/>/<see cref="SweptBlobs"/> are set by a repair pass.
/// </summary>
public sealed record VerifyReport(
    int TotalVersions,
    int OkVersions,
    int UnrestorableVersions,
    int MissingChunks,
    int OrphanBlobs,
    bool Deep,
    int RemovedVersions = 0,
    int SweptBlobs = 0);

/// <summary>
/// Integrity check + repair for the version store — the "is my history actually restorable, and
/// can I get space back if it isn't?" tooling a backup product must have. A restore reads a
/// version's chunks in order; if one is gone or corrupt the restore fails partway, so verify
/// proves every version resolves and repair QUARANTINES the ones that don't (removing only the
/// unrestorable version records, never touching good ones) after snapshotting the index.
/// </summary>
public static class StoreMaintenance
{
    /// <summary>
    /// Checks every version's chunks. Shallow (<paramref name="deep"/> = false) confirms each
    /// chunk exists on disk; deep additionally reads + re-hashes every chunk (catches on-disk
    /// corruption, at the cost of reading the whole store). Read-only — reports, changes nothing.
    /// </summary>
    public static VerifyReport Verify(VersionLog log, BlobStore blobs, bool deep = false)
    {
        IReadOnlyList<FileVersion> all = log.All;
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int unrestorable = 0, missing = 0;

        foreach (FileVersion v in all)
        {
            bool ok = true;
            foreach (string hex in v.Chunks)
            {
                referenced.Add(hex);
                if (!ChunkIsGood(blobs, hex, deep))
                {
                    missing++;
                    ok = false;
                }
            }

            if (!ok)
            {
                unrestorable++;
            }
        }

        int orphans = 0;
        foreach (BlobId id in blobs.EnumerateAll())
        {
            if (!referenced.Contains(id.Hex))
            {
                orphans++;
            }
        }

        return new VerifyReport(all.Count, all.Count - unrestorable, unrestorable, missing, orphans, deep);
    }

    /// <summary>
    /// Repairs the store: snapshots the index, removes every version that can't be fully restored
    /// (a missing/corrupt chunk), then sweeps orphaned blobs to reclaim space. Good versions are
    /// never touched. Returns a report of what was checked and removed. Run under the store's
    /// maintenance gate (via <see cref="VersionStore.RunExclusive{T}"/>) so it can't race a capture.
    /// </summary>
    public static VerifyReport Repair(VersionLog log, BlobStore blobs, bool deep = false)
    {
        log.Backup(); // known-good snapshot before any removal

        IReadOnlyList<FileVersion> all = log.All;
        var keep = new List<FileVersion>(all.Count);
        int unrestorable = 0, missing = 0;

        foreach (FileVersion v in all)
        {
            bool ok = true;
            foreach (string hex in v.Chunks)
            {
                if (!ChunkIsGood(blobs, hex, deep))
                {
                    missing++;
                    ok = false;
                }
            }

            if (ok)
            {
                keep.Add(v);
            }
            else
            {
                unrestorable++;
            }
        }

        int removed = all.Count - keep.Count;
        if (removed > 0)
        {
            log.Rewrite(keep);
        }

        int swept = Retention.Sweep(log, blobs);
        return new VerifyReport(all.Count, keep.Count, unrestorable, missing, OrphanBlobs: 0, deep, removed, swept);
    }

    private static bool ChunkIsGood(BlobStore blobs, string hex, bool deep)
    {
        BlobId id;
        try
        {
            id = BlobId.Parse(hex);
        }
        catch
        {
            return false; // malformed id in the index
        }

        if (!deep)
        {
            return blobs.Exists(id);
        }

        try
        {
            _ = blobs.Get(id); // re-hashes; throws on missing or corrupt
            return true;
        }
        catch
        {
            return false;
        }
    }
}
