namespace StepWind.Core.Storage;

/// <summary>
/// Rules that keep the store from growing forever — the thing naive versioning tools forget
/// until they eat the disk. Tiered like Time Machine: keep everything recent, then thin out
/// to hourly/daily/weekly as versions age, and enforce a hard age cap and per-file version
/// cap. Applied by <see cref="Retention"/>.
/// </summary>
public sealed class RetentionPolicy
{
    /// <summary>Keep every version from the last N hours (no thinning at all).</summary>
    public int KeepAllHours { get; set; } = 24;

    /// <summary>After that, keep at most one version per hour for this many days.</summary>
    public int HourlyDays { get; set; } = 7;

    /// <summary>Then one per day for this many days.</summary>
    public int DailyDays { get; set; } = 90;

    /// <summary>Hard cap: versions older than this are removed entirely.</summary>
    public int MaxAgeDays { get; set; } = 365;

    /// <summary>Never keep more than this many versions of a single file (newest win).</summary>
    public int MaxVersionsPerFile { get; set; } = 200;
}
