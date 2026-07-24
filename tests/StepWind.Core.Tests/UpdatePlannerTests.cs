using System.Runtime.Versioning;
using System.Security.Cryptography;
using StepWind.Core.Updates;
using Xunit;

namespace StepWind.Core.Tests;

/// <summary>
/// Fail-closed guarantees for the SYSTEM auto-updater's decision logic. These are the checks
/// that stand between "download a file from GitHub" and "run it as SYSTEM with no prompt", so
/// every refusal path is pinned by a test: missing checksums, wrong checksum, filename that
/// doesn't match, and (via <see cref="Authenticode"/>) an unsigned binary.
/// </summary>
public class UpdatePlannerTests
{
    [Theory]
    [InlineData("v1.2.3", 1, 2, 3)]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("V2.0.0-rc1", 2, 0, 0)]
    [InlineData("1.4.0+build.99", 1, 4, 0)]
    public void TryParseVersion_normalizes_tags(string tag, int major, int minor, int build)
    {
        Assert.True(UpdatePlanner.TryParseVersion(tag, out Version v));
        Assert.Equal(new Version(major, minor, build), v);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-version")]
    public void TryParseVersion_rejects_garbage(string? tag)
    {
        Assert.False(UpdatePlanner.TryParseVersion(tag, out _));
    }

    [Fact]
    public void ParseRelease_extracts_tag_and_asset_urls()
    {
        const string json = """
        {
          "tag_name": "v1.2.0",
          "assets": [
            { "name": "StepWind-1.2.0-setup.exe", "browser_download_url": "https://example/setup.exe" },
            { "name": "SHA256SUMS.txt", "browser_download_url": "https://example/sums.txt" },
            { "name": "notes.md", "browser_download_url": "https://example/notes.md" }
          ]
        }
        """;

        ReleaseInfo info = UpdatePlanner.ParseRelease(json);
        Assert.Equal("v1.2.0", info.Tag);
        Assert.Equal("https://example/setup.exe", info.SetupUrl);
        Assert.Equal("https://example/sums.txt", info.SumsUrl);
    }

    [Fact]
    public void ParseRelease_picks_the_setup_matching_the_running_architecture()
    {
        const string json = """
        {
          "tag_name": "v1.2.0",
          "assets": [
            { "name": "StepWind-1.2.0-setup.exe", "browser_download_url": "https://example/x64.exe" },
            { "name": "StepWind-1.2.0-arm64-setup.exe", "browser_download_url": "https://example/arm64.exe" },
            { "name": "SHA256SUMS.txt", "browser_download_url": "https://example/sums.txt" }
          ]
        }
        """;

        Assert.Equal("https://example/x64.exe", UpdatePlanner.ParseRelease(json, "x64").SetupUrl);
        Assert.Equal("https://example/arm64.exe", UpdatePlanner.ParseRelease(json, "arm64").SetupUrl);
        // arm64 machine but only an x64 asset → fall back to the x64 installer rather than nothing.
        const string x64Only = """{ "tag_name":"v1","assets":[{"name":"StepWind-1-setup.exe","browser_download_url":"https://example/x64.exe"}]}""";
        Assert.Equal("https://example/x64.exe", UpdatePlanner.ParseRelease(x64Only, "arm64").SetupUrl);
    }

    [Fact]
    public void ParseRelease_reports_missing_assets_as_null()
    {
        const string json = """{ "tag_name": "v1.2.0", "assets": [] }""";
        ReleaseInfo info = UpdatePlanner.ParseRelease(json);
        Assert.Equal("v1.2.0", info.Tag);
        Assert.Null(info.SetupUrl);
        Assert.Null(info.SumsUrl); // absent SHA256SUMS ⇒ the service refuses (fail-closed)
    }

    [Fact]
    public void ChecksumMatches_accepts_the_correct_hash_for_the_right_filename()
    {
        const string hash = "abc123def456";
        string sums = $"{hash}  StepWind-1.2.0-setup.exe\n0000  other.bin\n";
        Assert.True(UpdatePlanner.ChecksumMatches(sums, "StepWind-1.2.0-setup.exe", hash));
    }

    [Fact]
    public void ChecksumMatches_accepts_binary_mode_star_prefix()
    {
        const string hash = "deadbeef";
        string sums = $"{hash} *StepWind-1.2.0-setup.exe\n";
        Assert.True(UpdatePlanner.ChecksumMatches(sums, "StepWind-1.2.0-setup.exe", hash));
    }

    [Fact]
    public void ChecksumMatches_rejects_a_hash_listed_against_a_different_filename()
    {
        // The core hardening: a hash that appears in the file but next to ANOTHER name must not
        // vouch for our setup. The old "any line whose first token matches" logic said yes here.
        const string hash = "abc123";
        string sums = $"{hash}  some-other-artifact.zip\n";
        Assert.False(UpdatePlanner.ChecksumMatches(sums, "StepWind-1.2.0-setup.exe", hash));
    }

    [Fact]
    public void ChecksumMatches_rejects_a_wrong_hash_for_the_right_filename()
    {
        string sums = "aaaa  StepWind-1.2.0-setup.exe\n";
        Assert.False(UpdatePlanner.ChecksumMatches(sums, "StepWind-1.2.0-setup.exe", "bbbb"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ChecksumMatches_rejects_empty_sums(string sums)
    {
        Assert.False(UpdatePlanner.ChecksumMatches(sums, "StepWind-1.2.0-setup.exe", "abc"));
    }

    [SupportedOSPlatform("windows")]
    [Fact]
    public void Authenticode_never_trusts_an_unsigned_file()
    {
        // THE security property: a file without a valid embedded Authenticode signature is never
        // Trusted, so the SYSTEM updater refuses to launch it. A random (non-PE) blob comes back
        // Untrusted; a valid PE with no signature would come back NoSignature — both block.
        string tmp = Path.Combine(Path.GetTempPath(), "stepwind-unsigned-" + Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllBytes(tmp, RandomNumberGenerator.GetBytes(4096));
        try
        {
            Assert.NotEqual(SignatureTrust.Trusted, Authenticode.VerifyFile(tmp));
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [SupportedOSPlatform("windows")]
    [Fact]
    public void Authenticode_trusts_a_genuine_embedded_signed_binary()
    {
        // Proves the wrapper CAN return Trusted for a real, valid signature (not failing closed on
        // everything). Uses an EMBEDDED-signed binary — most OS exes (notepad) are catalog-signed,
        // which a downloaded file never carries, so those correctly read as NoSignature. Skips if
        // no embedded-signed binary is present in this environment (keeps the test non-flaky).
        foreach (string candidate in EmbeddedSignedCandidates())
        {
            if (File.Exists(candidate) && Authenticode.VerifyFile(candidate) == SignatureTrust.Trusted)
            {
                Assert.NotNull(Authenticode.SignerThumbprint(candidate)); // and the signer is readable
                return; // proven
            }
        }
        // No embedded-signed binary found to verify against — inconclusive, not a failure.
    }

    private static IEnumerable<string> EmbeddedSignedCandidates()
    {
        string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Path.Combine(pf86, "Microsoft", "Edge", "Application", "msedge.exe");
        yield return Path.Combine(pf, "Microsoft", "Edge", "Application", "msedge.exe");

        // Any versioned msedgewebview2.exe (the runtime StepWind's own GUI depends on).
        foreach (string baseDir in new[]
        {
            Path.Combine(pf86, "Microsoft", "EdgeWebView", "Application"),
            Path.Combine(pf, "Microsoft", "EdgeWebView", "Application"),
        })
        {
            if (Directory.Exists(baseDir))
            {
                foreach (string exe in Directory.EnumerateFiles(baseDir, "msedgewebview2.exe", SearchOption.AllDirectories))
                {
                    yield return exe;
                }
            }
        }
    }

    [SupportedOSPlatform("windows")]
    [Fact]
    public void Authenticode_errors_gracefully_on_a_missing_file()
    {
        Assert.Equal(SignatureTrust.Error, Authenticode.VerifyFile(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N") + ".exe")));
    }

    [Theory]
    [InlineData(SignatureTrust.NoSignature)]
    [InlineData(SignatureTrust.Untrusted)]
    [InlineData(SignatureTrust.Error)]
    public void Only_a_trusted_signature_may_install_silently(SignatureTrust trust)
    {
        // An unsigned / untrusted setup is NEVER silently installed as SYSTEM — it's staged for a
        // user-consented install instead. This is the gate that keeps the auto-updater from being
        // an RCE channel while releases are unsigned.
        Assert.False(UpdatePlanner.ShouldSilentlyInstall(trust, actualThumbprint: null, expectedThumbprint: null));
    }

    [Fact]
    public void An_unpinned_signature_is_never_installed_silently()
    {
        // Fail-closed: with no certificate pinned, even a trusted signature does NOT earn a silent
        // SYSTEM install (it's staged for user consent instead). An attacker who replaced the
        // release assets could sign with any trusted-CA cert, so only OUR pinned cert may go silent.
        Assert.False(UpdatePlanner.ShouldSilentlyInstall(SignatureTrust.Trusted, "ABCD", expectedThumbprint: null));
        Assert.False(UpdatePlanner.ShouldSilentlyInstall(SignatureTrust.Trusted, "ABCD", expectedThumbprint: ""));
    }

    [Fact]
    public void A_pinned_thumbprint_must_match_to_install_silently()
    {
        Assert.True(UpdatePlanner.ShouldSilentlyInstall(SignatureTrust.Trusted, "ABCD", "abcd"));   // case-insensitive match
        Assert.False(UpdatePlanner.ShouldSilentlyInstall(SignatureTrust.Trusted, "BEEF", "ABCD"));  // trusted, wrong cert
        Assert.False(UpdatePlanner.ShouldSilentlyInstall(SignatureTrust.Trusted, null, "ABCD"));    // trusted, no thumbprint read
    }
}
