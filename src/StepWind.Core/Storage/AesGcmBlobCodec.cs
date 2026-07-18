using System.IO.Compression;
using System.Security.Cryptography;

namespace StepWind.Core.Storage;

/// <summary>
/// Optional passphrase encryption: compress, then AES-256-GCM. The key is derived once from
/// the user's passphrase + a per-repo random salt (see <see cref="DeriveKey"/>); a fresh
/// random 12-byte nonce is generated per blob and prepended, with the 16-byte GCM tag
/// appended, so every stored blob authenticates itself on read (tamper = failure, not
/// silent garbage). Losing the passphrase means the history can't be read — the UI warns
/// about this before enabling it.
///
/// On-disk blob layout:  [nonce:12][ciphertext:N][tag:16]
/// </summary>
public sealed class AesGcmBlobCodec : IBlobCodec
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[] _key;

    public AesGcmBlobCodec(byte[] key)
    {
        if (key.Length != 32)
        {
            throw new ArgumentException("AES-256 requires a 32-byte key.", nameof(key));
        }

        _key = key;
    }

    public string Id => "aesgcm-deflate";

    /// <summary>Derives a 256-bit key from a passphrase and the repo's stored salt (PBKDF2-SHA256).</summary>
    public static byte[] DeriveKey(string passphrase, byte[] salt, int iterations = 600_000)
        => Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, iterations, HashAlgorithmName.SHA256, 32);

    public byte[] Encode(ReadOnlySpan<byte> plaintext)
    {
        byte[] compressed;
        using (var buffer = new MemoryStream())
        {
            using (var deflate = new DeflateStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
            {
                deflate.Write(plaintext);
            }

            compressed = buffer.ToArray();
        }

        byte[] output = new byte[NonceSize + compressed.Length + TagSize];
        Span<byte> nonce = output.AsSpan(0, NonceSize);
        Span<byte> cipher = output.AsSpan(NonceSize, compressed.Length);
        Span<byte> tag = output.AsSpan(NonceSize + compressed.Length, TagSize);

        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, compressed, cipher, tag);
        return output;
    }

    public byte[] Decode(ReadOnlySpan<byte> stored)
    {
        if (stored.Length < NonceSize + TagSize)
        {
            throw new CryptographicException("Encrypted blob is too short to be valid.");
        }

        ReadOnlySpan<byte> nonce = stored[..NonceSize];
        ReadOnlySpan<byte> cipher = stored[NonceSize..^TagSize];
        ReadOnlySpan<byte> tag = stored[^TagSize..];

        byte[] compressed = new byte[cipher.Length];
        using (var aes = new AesGcm(_key, TagSize))
        {
            aes.Decrypt(nonce, cipher, tag, compressed); // throws if tampered / wrong key
        }

        using var input = new MemoryStream(compressed);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        deflate.CopyTo(output);
        return output.ToArray();
    }
}
