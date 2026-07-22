using System.Buffers.Binary;
using System.Security.Cryptography;

namespace StepWind.Core.Storage;

/// <summary>
/// Exports and imports a passphrase-protected copy of the store's AES key — the answer to the
/// one way encryption-at-rest can lose your data: the live key is sealed with machine-scope
/// DPAPI, so reinstalling Windows or moving the disk to another machine makes the sealed key
/// unwrappable and the encrypted history unreadable forever. A recovery key breaks that: the
/// user exports the 32-byte store key wrapped under a passphrase they choose and keeps it
/// somewhere safe; on a new machine, that file + the passphrase reconstruct the key and an
/// offline restore can read the store again.
///
/// Format (binary): [magic 8]["SWKREC1\0"] [iterations int32-LE] [salt 16] [nonce 12]
///                  [ciphertext 32] [tag 16].  The passphrase derives a key via PBKDF2-SHA256
/// and unwraps the store key with AES-256-GCM (authenticated: a wrong passphrase fails cleanly).
/// </summary>
public static class KeyRecovery
{
    private static readonly byte[] Magic = "SWKREC1\0"u8.ToArray();
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private const int DefaultIterations = 600_000;

    /// <summary>Wraps <paramref name="storeKey"/> (32 bytes) under <paramref name="passphrase"/> into a recovery blob.</summary>
    public static byte[] Export(byte[] storeKey, string passphrase, int iterations = DefaultIterations)
    {
        ArgumentNullException.ThrowIfNull(storeKey);
        if (storeKey.Length != KeySize)
        {
            throw new ArgumentException("Store key must be 32 bytes.", nameof(storeKey));
        }

        if (string.IsNullOrEmpty(passphrase))
        {
            throw new ArgumentException("A recovery passphrase is required.", nameof(passphrase));
        }

        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
        byte[] derived = AesGcmBlobCodec.DeriveKey(passphrase, salt, iterations);

        byte[] cipher = new byte[KeySize];
        byte[] tag = new byte[TagSize];
        using (var aes = new AesGcm(derived, TagSize))
        {
            aes.Encrypt(nonce, storeKey, cipher, tag);
        }

        byte[] blob = new byte[Magic.Length + 4 + SaltSize + NonceSize + KeySize + TagSize];
        int o = 0;
        Magic.CopyTo(blob, o); o += Magic.Length;
        BinaryPrimitives.WriteInt32LittleEndian(blob.AsSpan(o), iterations); o += 4;
        salt.CopyTo(blob, o); o += SaltSize;
        nonce.CopyTo(blob, o); o += NonceSize;
        cipher.CopyTo(blob, o); o += KeySize;
        tag.CopyTo(blob, o);
        return blob;
    }

    /// <summary>
    /// Recovers the 32-byte store key from a recovery blob + passphrase. Throws
    /// <see cref="CryptographicException"/> on a wrong passphrase or a tampered/short blob.
    /// </summary>
    public static byte[] Import(byte[] blob, string passphrase)
    {
        ArgumentNullException.ThrowIfNull(blob);
        int expected = Magic.Length + 4 + SaltSize + NonceSize + KeySize + TagSize;
        if (blob.Length != expected || !blob.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            throw new CryptographicException("Not a valid StepWind recovery key file.");
        }

        int o = Magic.Length;
        int iterations = BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(o)); o += 4;
        if (iterations is < 10_000 or > 10_000_000)
        {
            throw new CryptographicException("Recovery key file has an implausible iteration count.");
        }

        byte[] salt = blob[o..(o + SaltSize)]; o += SaltSize;
        byte[] nonce = blob[o..(o + NonceSize)]; o += NonceSize;
        byte[] cipher = blob[o..(o + KeySize)]; o += KeySize;
        byte[] tag = blob[o..(o + TagSize)];

        byte[] derived = AesGcmBlobCodec.DeriveKey(passphrase, salt, iterations);
        byte[] key = new byte[KeySize];
        using var aes = new AesGcm(derived, TagSize);
        aes.Decrypt(nonce, cipher, tag, key); // throws CryptographicException on wrong passphrase
        return key;
    }
}
