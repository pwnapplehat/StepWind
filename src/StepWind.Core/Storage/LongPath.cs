namespace StepWind.Core.Storage;

/// <summary>
/// Applies the Win32 extended-length prefix (<c>\\?\</c>) to a fully-qualified path that's long
/// enough to trip the legacy 260-char MAX_PATH limit, so StepWind can read and version files in
/// deep folder trees (which developers hit constantly). The paths StepWind opens come from
/// FileSystemWatcher and directory enumeration — already normalized and absolute — so prefixing
/// is safe (it deliberately skips normalization, which is exactly what we want here).
/// </summary>
public static class LongPath
{
    // Comfortably below MAX_PATH (260) so we opt in before any API rejects the path.
    private const int Threshold = 248;

    /// <summary>Returns <paramref name="path"/> extended-length-prefixed when it needs it, else unchanged.</summary>
    public static string Of(string path)
    {
        if (string.IsNullOrEmpty(path)
            || path.Length < Threshold
            || path.StartsWith(@"\\?\", StringComparison.Ordinal)
            || !Path.IsPathFullyQualified(path))
        {
            return path;
        }

        // UNC (\\server\share\...) uses the \\?\UNC\server\share\... form.
        return path.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + path[2..]
            : @"\\?\" + path;
    }
}
