using System.Text;

namespace StepWind.Core.Storage;

/// <summary>
/// Encrypts/decrypts a single line of the version index (<c>versions.jsonl</c>). When the store is
/// encrypted, this hides the metadata the blob encryption doesn't — file names, paths, and dates —
/// from anyone who can read the store folder offline (a stolen/moved drive). Line-oriented so the
/// index stays append-only and crash-safe: each line is independently encrypted, a truncated last
/// line is simply skipped, and a plaintext line (from before migration) is still readable.
/// </summary>
public interface IIndexCipher
{
    /// <summary>Encrypts one JSON line to an opaque token (base64 — never starts with '{').</summary>
    string Encrypt(string plaintextLine);

    /// <summary>Decrypts a token from <see cref="Encrypt"/> back to the JSON line.</summary>
    string Decrypt(string token);
}

/// <summary>
/// Index cipher backed by an <see cref="IBlobCodec"/> (in practice <see cref="AesGcmBlobCodec"/>),
/// so index lines are protected with the same authenticated AES-256-GCM as the blobs — no separate
/// crypto to audit. The token is base64 of the codec's output; base64 never begins with '{', so a
/// reader can tell an encrypted line from a legacy plaintext JSON line at a glance.
/// </summary>
public sealed class BlobCodecIndexCipher(IBlobCodec codec) : IIndexCipher
{
    public string Encrypt(string plaintextLine)
        => Convert.ToBase64String(codec.Encode(Encoding.UTF8.GetBytes(plaintextLine)));

    public string Decrypt(string token)
        => Encoding.UTF8.GetString(codec.Decode(Convert.FromBase64String(token)));
}
