using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using StepWind.Core.Updates;

namespace StepWind.Service;

/// <summary>
/// Automatic updater driven by the SYSTEM service. Because the service is already elevated it
/// can apply updates with no UAC prompt — which is exactly why every step here is FAIL-CLOSED:
/// running an unverified installer as SYSTEM would be a remote-code-execution channel, so an
/// update is launched ONLY when all of the following hold:
///
///   1. the release advertises a newer version and a setup asset;
///   2. the release publishes SHA256SUMS.txt (absent ⇒ refuse — never "assume good");
///   3. the downloaded setup's SHA-256 matches the published checksum FOR ITS FILENAME;
///   4. the downloaded setup carries a valid Authenticode signature that chains to a trusted
///      root (and, once <see cref="ExpectedSignerThumbprint"/> is pinned, is OUR certificate).
///
/// A GitHub-published checksum is not an independent root of trust (an attacker who can replace
/// the setup asset can replace the sums too), so the code signature in (4) is the real gate.
/// Until releases are code-signed, (4) fails and silent auto-update simply does not run — the
/// safe default. Rollback of a failed install is owned by the INSTALLER (Inno Setup [Code]),
/// not by this service: the installer stops this very process before swapping files, so the
/// service cannot roll itself back.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UpdateService
{
    private const string Owner = "pwnapplehat";
    private const string Repo = "StepWind";
    private const string LatestApi = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";

    /// <summary>
    /// StepWind's release-signing certificate thumbprint (uppercase hex, no spaces). While empty,
    /// any signature that chains to a trusted root is accepted; set this once releases are signed
    /// to PIN updates to our own certificate and reject every other publisher.
    /// </summary>
    private const string ExpectedSignerThumbprint = "";

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
            return UpdatePlanner.TryParseVersion(raw, out Version v) ? v : new Version(1, 0, 0);
        }
    }

    /// <summary>Checks for a newer release and, if found and fully verified, installs it silently.</summary>
    public async Task<UpdateOutcome> CheckAndApplyAsync(CancellationToken ct = default)
    {
        try
        {
            using HttpResponseMessage resp = await _http.GetAsync(LatestApi, ct);
            if (!resp.IsSuccessStatusCode)
            {
                return UpdateOutcome.Error;
            }

            ReleaseInfo release = UpdatePlanner.ParseRelease(await resp.Content.ReadAsStringAsync(ct));

            if (!UpdatePlanner.TryParseVersion(release.Tag, out Version latest) || latest <= CurrentVersion)
            {
                return UpdateOutcome.UpToDate;
            }

            _log($"update available: {latest.ToString(3)} (running {CurrentVersion.ToString(3)})");

            if (release.SetupUrl is null)
            {
                _log("update skipped: no setup asset on the release");
                return UpdateOutcome.NoSetupAsset;
            }

            // (2) Fail closed on a missing checksum list. An installer with no published SHA256SUMS
            // is refused outright — we never run an unverified binary as SYSTEM.
            if (release.SumsUrl is null)
            {
                _log("update ABORTED: release has no SHA256SUMS.txt to verify the setup against");
                return UpdateOutcome.NoChecksums;
            }

            string dir = Path.Combine(Path.GetTempPath(), "StepWindUpdate");
            Directory.CreateDirectory(dir);
            string setupPath = Path.Combine(dir, $"StepWind-{latest.ToString(3)}-setup.exe");

            await Download(release.SetupUrl, setupPath, ct);

            // (3) Checksum must match for THIS filename.
            string sumsText = await _http.GetStringAsync(release.SumsUrl, ct);
            string actual = await Sha256HexAsync(setupPath, ct);
            if (!UpdatePlanner.ChecksumMatches(sumsText, Path.GetFileName(setupPath), actual))
            {
                _log("update ABORTED: SHA-256 of the downloaded setup did not match the published checksum");
                TryDelete(setupPath);
                return UpdateOutcome.ChecksumMismatch;
            }

            // (4) Authenticode is the real root of trust. A checksum from the same release an
            // attacker would tamper with proves only integrity, not authenticity.
            SignatureTrust trust = Authenticode.VerifyFile(setupPath);
            if (trust != SignatureTrust.Trusted)
            {
                _log($"update ABORTED: setup is not signed by a trusted publisher ({trust}); silent update stays disabled until releases are code-signed");
                TryDelete(setupPath);
                return UpdateOutcome.Unsigned;
            }

            if (ExpectedSignerThumbprint.Length > 0)
            {
                string? thumb = Authenticode.SignerThumbprint(setupPath);
                if (!string.Equals(thumb, ExpectedSignerThumbprint, StringComparison.OrdinalIgnoreCase))
                {
                    _log($"update ABORTED: setup signer thumbprint '{thumb}' is not StepWind's pinned certificate");
                    TryDelete(setupPath);
                    return UpdateOutcome.Unsigned;
                }
            }

            _log("setup verified (checksum + trusted Authenticode signature); launching silent update");
            // The installer stops this service, backs up the current install, swaps files, and —
            // if the new service won't start — restores the backup (see installer/stepwind.iss
            // [Code]). We only ever hand it a verified, signed binary.
            Process.Start(new ProcessStartInfo(setupPath, "/VERYSILENT /NORESTART /SUPPRESSMSGBOXES")
            {
                UseShellExecute = false,
            });
            return UpdateOutcome.Launched;
        }
        catch (Exception ex)
        {
            _log("update check failed: " + ex.Message); // never fatal — protection keeps running
            return UpdateOutcome.Error;
        }
    }

    private async Task Download(string url, string dest, CancellationToken ct)
    {
        using HttpResponseMessage resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using FileStream fs = File.Create(dest);
        await resp.Content.CopyToAsync(fs, ct);
    }

    private static async Task<string> Sha256HexAsync(string filePath, CancellationToken ct)
    {
        await using FileStream fs = File.OpenRead(filePath);
        return Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
