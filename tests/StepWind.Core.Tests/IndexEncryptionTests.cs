using System.Security.Cryptography;
using StepWind.Core.Storage;
using Xunit;

namespace StepWind.Core.Tests;

/// <summary>
/// P1-3: optional encryption of the version INDEX (metadata: names/paths/dates), not just blob
/// content. The critical safety property is that toggling it can NEVER orphan history — the key
/// is used for reading regardless of the write mode, so encrypted lines always stay readable, and
/// a mixed (plaintext + encrypted) file loads cleanly. These pin exactly that.
/// </summary>
public class IndexEncryptionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sw-indexcrypt", Guid.NewGuid().ToString("N"));
    private readonly IIndexCipher _cipher;

    public IndexEncryptionTests()
    {
        Directory.CreateDirectory(_root);
        _cipher = new BlobCodecIndexCipher(new AesGcmBlobCodec(RandomNumberGenerator.GetBytes(32)));
    }

    private string LogPath => Path.Combine(_root, "versions.jsonl");
    private static FileVersion V(string rel) => new()
    {
        RelativePath = rel, CapturedUtc = DateTime.UtcNow, Size = 3, Chunks = ["deadbeef"], Reason = "change",
    };

    [Fact]
    public void Encrypted_index_hides_metadata_on_disk_but_reads_back()
    {
        var log = new VersionLog(LogPath, _cipher, encryptOnWrite: true);
        log.Append(V("Secret/passwords.txt"));

        // On disk: the filename must NOT appear (metadata is encrypted), and the line isn't JSON.
        string raw = File.ReadAllText(LogPath);
        Assert.DoesNotContain("passwords.txt", raw);
        Assert.DoesNotContain("Secret", raw);
        Assert.False(raw.TrimStart().StartsWith('{'));

        // Reopened with the key, history is intact.
        var reopened = new VersionLog(LogPath, _cipher, encryptOnWrite: true);
        Assert.Single(reopened.History("Secret/passwords.txt"));
    }

    [Fact]
    public void Turning_index_encryption_OFF_still_reads_previously_encrypted_lines()
    {
        // The catastrophe to prevent: disabling index encryption must not lose the encrypted lines.
        var enc = new VersionLog(LogPath, _cipher, encryptOnWrite: true);
        enc.Append(V("a.txt"));
        enc.Append(V("b.txt"));

        // Reopen with encryptOnWrite:false but the SAME cipher available for reading.
        var off = new VersionLog(LogPath, _cipher, encryptOnWrite: false);
        Assert.Single(off.History("a.txt"));
        Assert.Single(off.History("b.txt"));

        // A new append is now plaintext, and the file is mixed — still fully readable.
        off.Append(V("c.txt"));
        var mixed = new VersionLog(LogPath, _cipher, encryptOnWrite: false);
        Assert.Single(mixed.History("a.txt")); // was encrypted
        Assert.Single(mixed.History("c.txt")); // was plaintext
    }

    [Fact]
    public void A_plaintext_index_is_read_even_when_a_cipher_is_present()
    {
        // Enabling index encryption on an existing (plaintext) store must keep reading old lines.
        var plain = new VersionLog(LogPath); // no cipher — writes plaintext
        plain.Append(V("legacy.txt"));

        var withCipher = new VersionLog(LogPath, _cipher, encryptOnWrite: true);
        Assert.Single(withCipher.History("legacy.txt")); // plaintext line still read
    }

    [Fact]
    public void Rewrite_converges_a_mixed_file_to_the_current_mode()
    {
        // Build a mixed file: one plaintext (no cipher) then one encrypted append.
        new VersionLog(LogPath).Append(V("plain.txt"));
        var enc = new VersionLog(LogPath, _cipher, encryptOnWrite: true);
        enc.Append(V("crypt.txt"));

        // Rewrite (what retention/repair does) re-emits every kept line in the current mode.
        enc.Rewrite([.. enc.All]);

        // Now every line is encrypted: no plaintext filename remains, and all history survives.
        string raw = File.ReadAllText(LogPath);
        Assert.DoesNotContain("plain.txt", raw);
        Assert.DoesNotContain("crypt.txt", raw);
        var reopened = new VersionLog(LogPath, _cipher, encryptOnWrite: true);
        Assert.Equal(2, reopened.All.Count);
    }

    [Fact]
    public void A_truncated_final_encrypted_line_is_skipped_not_fatal()
    {
        var enc = new VersionLog(LogPath, _cipher, encryptOnWrite: true);
        enc.Append(V("good.txt"));
        File.AppendAllText(LogPath, "dGhpcyBpcyBub3QgYSB2YWxpZCB0b2tlbg\n"); // garbage token

        var reopened = new VersionLog(LogPath, _cipher, encryptOnWrite: true);
        Assert.Single(reopened.History("good.txt")); // the good line survives; garbage skipped
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
