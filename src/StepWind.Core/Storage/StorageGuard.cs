namespace StepWind.Core.Storage;

/// <summary>The current storage headroom decision, surfaced to the UI/logs.</summary>
public sealed record StorageState(bool Paused, string? Reason, long FreeBytes, long StoreBytes, long MinFreeBytes, long MaxStoreBytes);

/// <summary>
/// Keeps StepWind from silently strangling the disk — the failure that made people distrust
/// Windows File History (it just stopped backing up, quietly). Before every capture the engine
/// asks this guard whether there's headroom; when the store's drive drops below a free-space
/// floor, or the store exceeds an optional size cap, captures PAUSE (loudly, via status), an
/// emergency retention prune runs to try to win the space back, and the pause lifts automatically
/// once there's room again. Pausing is safe: the startup/periodic reconcile re-captures anything
/// that changed while paused, so nothing is lost — only deferred until it can be stored safely.
/// </summary>
public sealed class StorageGuard(string storeRoot, long minFreeBytes, long maxStoreBytes)
{
    /// <summary>Default free-space floor: pause capturing when the store's drive drops below 1 GiB.</summary>
    public const long DefaultMinFreeBytes = 1L * 1024 * 1024 * 1024;

    private readonly string _storeRoot = storeRoot;

    public long MinFreeBytes { get; } = minFreeBytes > 0 ? minFreeBytes : DefaultMinFreeBytes;

    /// <summary>Optional hard cap on the store's own size (0 = unlimited).</summary>
    public long MaxStoreBytes { get; } = Math.Max(0, maxStoreBytes);

    /// <summary>Evaluates headroom given the store's current on-disk size. Never throws.</summary>
    public StorageState Evaluate(long storeBytes)
    {
        long free = FreeBytesOnStoreDrive();

        if (free >= 0 && free < MinFreeBytes)
        {
            return new StorageState(true,
                $"Low disk space — only {Format(free)} free on the drive holding your history (need {Format(MinFreeBytes)}). Capturing is paused to protect your existing versions; free up space and it resumes automatically.",
                free, storeBytes, MinFreeBytes, MaxStoreBytes);
        }

        if (MaxStoreBytes > 0 && storeBytes >= MaxStoreBytes)
        {
            return new StorageState(true,
                $"History store reached its {Format(MaxStoreBytes)} limit. Capturing is paused; raise the limit in Settings or let retention thin older versions.",
                free, storeBytes, MinFreeBytes, MaxStoreBytes);
        }

        return new StorageState(false, null, free, storeBytes, MinFreeBytes, MaxStoreBytes);
    }

    /// <summary>Available free bytes on the store's drive, or -1 if it can't be determined.</summary>
    public long FreeBytesOnStoreDrive()
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(_storeRoot));
            if (string.IsNullOrEmpty(root))
            {
                return -1;
            }

            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch
        {
            return -1; // unknown — treated as "don't pause on free space" (fail-open for availability)
        }
    }

    private static string Format(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1)
        {
            v /= 1024;
            u++;
        }

        return $"{v:0.#} {units[u]}";
    }
}
