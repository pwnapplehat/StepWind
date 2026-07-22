using System.Text.Json;

namespace StepWind.Core.Updates;

/// <summary>Why an update was applied, skipped, or refused — every branch is explicit and logged.</summary>
public enum UpdateOutcome
{
    /// <summary>No release info, or the latest release is not newer than what's running.</summary>
    UpToDate,

    /// <summary>The release is missing the setup asset.</summary>
    NoSetupAsset,

    /// <summary>The release is missing SHA256SUMS.txt — refused (fail-closed; no unverified install).</summary>
    NoChecksums,

    /// <summary>The downloaded setup's SHA-256 did not match the published checksum for its filename.</summary>
    ChecksumMismatch,

    /// <summary>The downloaded setup is not Authenticode-signed by a trusted publisher — refused.</summary>
    Unsigned,

    /// <summary>Everything verified; the installer was launched.</summary>
    Launched,

    /// <summary>A network/IO error occurred; protection keeps running and we retry later.</summary>
    Error,
}

/// <summary>
/// The pure, side-effect-free decision logic for the auto-updater: parse the release, choose
/// assets, and verify a checksum against the published sums by FILENAME. Kept separate from
/// <c>UpdateService</c> (which does the network + process IO) so every fail-closed branch is
/// unit-testable without a network or a signed binary.
/// </summary>
public static class UpdatePlanner
{
    /// <summary>Normalizes a tag/version string ("v1.2.3", "1.2.3-rc1+build") to a 3-part <see cref="Version"/>.</summary>
    public static bool TryParseVersion(string? s, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(s))
        {
            return false;
        }

        string core = s.Trim().TrimStart('v', 'V').Split('+', '-')[0];
        if (!Version.TryParse(core, out Version? parsed) || parsed is null)
        {
            return false;
        }

        version = new Version(Math.Max(0, parsed.Major), Math.Max(0, parsed.Minor), Math.Max(0, parsed.Build));
        return true;
    }

    /// <summary>Pulls the tag_name and (setup, sums) asset download URLs out of a GitHub "latest release" JSON body.</summary>
    public static ReleaseInfo ParseRelease(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        string? tag = root.TryGetProperty("tag_name", out JsonElement t) ? t.GetString() : null;
        string? setup = null, sums = null;

        if (root.TryGetProperty("assets", out JsonElement assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement a in assets.EnumerateArray())
            {
                string? name = a.TryGetProperty("name", out JsonElement n) ? n.GetString() : null;
                string? url = a.TryGetProperty("browser_download_url", out JsonElement u) ? u.GetString() : null;
                if (name is null || url is null)
                {
                    continue;
                }

                if (name.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase))
                {
                    setup = url;
                }
                else if (name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
                {
                    sums = url;
                }
            }
        }

        return new ReleaseInfo(tag, setup, sums);
    }

    /// <summary>
    /// Verifies that <paramref name="sumsText"/> contains a line for <paramref name="fileName"/>
    /// whose hash equals <paramref name="actualHashHex"/>. Matching is by FILENAME, not "any line
    /// with this hash" — a checksum list must vouch for THIS file by name, so a hash that happens
    /// to appear next to a different filename never counts. Accepts the common
    /// "&lt;hex&gt;  name" and "&lt;hex&gt; *name" (binary-mode) shapes.
    /// </summary>
    public static bool ChecksumMatches(string sumsText, string fileName, string actualHashHex)
    {
        if (string.IsNullOrWhiteSpace(sumsText) || string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(actualHashHex))
        {
            return false;
        }

        string targetName = Path.GetFileName(fileName);
        foreach (string raw in sumsText.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                continue;
            }

            string hash = parts[0];
            // The name is the remainder; sha256sum binary mode prefixes it with '*'.
            string name = string.Join(' ', parts[1..]).TrimStart('*');
            name = Path.GetFileName(name);

            if (name.Equals(targetName, StringComparison.OrdinalIgnoreCase)
                && hash.Equals(actualHashHex, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>Parsed "latest release" facts: the tag and the two asset URLs we care about.</summary>
public sealed record ReleaseInfo(string? Tag, string? SetupUrl, string? SumsUrl);
