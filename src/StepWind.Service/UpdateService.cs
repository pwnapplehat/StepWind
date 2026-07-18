using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;

namespace StepWind.Service;

/// <summary>
/// Fully automatic, silent updater — driven by the SYSTEM service, which is the neat part:
/// because the service is already elevated, it applies updates with ZERO UAC prompts. Unlike
/// BitBroom (a lone GUI exe that hot-swaps itself), StepWind is a service + GUI + shared DLLs,
/// so the safe way to update is to download the new signed setup .exe, verify its SHA-256
/// against the release's SHA256SUMS.txt, and run it silently — the installer already knows how
/// to stop the service, replace every file, and restart cleanly.
///
/// One opt-out check at startup, then daily. Network is the only outbound traffic in StepWind.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UpdateService
{
    private const string Owner = "pwnapplehat";
    private const string Repo = "StepWind";
    private const string LatestApi = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";

    private readonly Action<string> _log;
    private readonly HttpClient _http;

    public UpdateService(Action<string> log)
    {
        _log = log;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"StepWind/{CurrentVersion.ToString(3)} (+https://github.com/{Owner}/{Repo})");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public static Version CurrentVersion
    {
        get
        {
            string raw = typeof(UpdateService).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0.0";
            return TryNormalize(raw, out Version? v) ? v! : new Version(1, 0, 0);
        }
    }

    /// <summary>Checks for a newer release and, if found, downloads + verifies + installs it silently.</summary>
    public async Task CheckAndApplyAsync(CancellationToken ct = default)
    {
        try
        {
            using HttpResponseMessage resp = await _http.GetAsync(LatestApi, ct);
            if (!resp.IsSuccessStatusCode)
            {
                return;
            }

            using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            JsonElement root = doc.RootElement;

            string? tag = root.TryGetProperty("tag_name", out JsonElement t) ? t.GetString() : null;
            if (tag is null || !TryNormalize(tag, out Version? latest) || latest is null || latest <= CurrentVersion)
            {
                return; // up to date
            }

            _log($"update available: {latest.ToString(3)} (running {CurrentVersion.ToString(3)})");

            (string? setupUrl, string? sumsUrl) = FindAssets(root);
            if (setupUrl is null)
            {
                _log("update skipped: no setup asset on the release");
                return;
            }

            string dir = Path.Combine(Path.GetTempPath(), "StepWindUpdate");
            Directory.CreateDirectory(dir);
            string setupPath = Path.Combine(dir, $"StepWind-{latest.ToString(3)}-setup.exe");

            await Download(setupUrl, setupPath, ct);

            if (sumsUrl is not null && !await VerifyHash(setupPath, sumsUrl, ct))
            {
                _log("update ABORTED: SHA-256 of the downloaded setup did not match the published checksum");
                TryDelete(setupPath);
                return;
            }

            _log("checksum verified; launching silent update");
            // The SYSTEM service runs the installer silently — no UAC. The installer stops
            // this service, swaps files, and restarts it; VERYSILENT + NORESTART keep it quiet.
            Process.Start(new ProcessStartInfo(setupPath, "/VERYSILENT /NORESTART /SUPPRESSMSGBOXES")
            {
                UseShellExecute = false,
            });
        }
        catch (Exception ex)
        {
            _log("update check failed: " + ex.Message); // never fatal — protection keeps running
        }
    }

    private static (string? Setup, string? Sums) FindAssets(JsonElement release)
    {
        string? setup = null, sums = null;
        if (release.TryGetProperty("assets", out JsonElement assets) && assets.ValueKind == JsonValueKind.Array)
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

        return (setup, sums);
    }

    private async Task Download(string url, string dest, CancellationToken ct)
    {
        using HttpResponseMessage resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using FileStream fs = File.Create(dest);
        await resp.Content.CopyToAsync(fs, ct);
    }

    private async Task<bool> VerifyHash(string filePath, string sumsUrl, CancellationToken ct)
    {
        string sums = await _http.GetStringAsync(sumsUrl, ct);
        string actual;
        await using (FileStream fs = File.OpenRead(filePath))
        {
            actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
        }

        string wantedName = Path.GetFileName(filePath);
        foreach (string raw in sums.Split('\n'))
        {
            string line = raw.Trim();
            // "<hex>  <name>" — accept if the hash matches our file's, regardless of exact name spacing.
            string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[0].Equals(actual, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        _log($"no matching checksum for {wantedName}");
        return false;
    }

    private static bool TryNormalize(string s, out Version? v)
    {
        v = null;
        string core = s.Trim().TrimStart('v', 'V').Split('+', '-')[0];
        if (!Version.TryParse(core, out Version? parsed))
        {
            return false;
        }

        v = new Version(Math.Max(0, parsed.Major), Math.Max(0, parsed.Minor), Math.Max(0, parsed.Build));
        return true;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
