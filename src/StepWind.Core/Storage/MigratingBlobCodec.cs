using System.Runtime.Versioning;

namespace StepWind.Core.Storage;

/// <summary>
/// The codec that makes encryption a live, toggleable setting instead of a create-time
/// decision. It holds both formats — plain (deflate) and cipher (AES-256-GCM) — and:
///
///   • encodes new blobs with whichever the user currently wants;
///   • decodes blobs of EITHER format, so a store that's mid-transition (or interrupted by
///     a crash mid-re-encode) is always fully readable — no flag day, no unreadable history;
///   • lazily creates the cipher (and its DPAPI-sealed key) the first time it's needed, so
///     enabling encryption at runtime needs no restart.
///
/// Safety: a wrong-format decode is not trusted to fail loudly on its own — AES-GCM always
/// authenticates, but raw deflate has no checksum, so garbage output is conceivable. That's
/// why <see cref="BlobStore.Get"/> re-hashes every decode against the blob's content id and,
/// on any failure, retries via <see cref="DecodeAlternate"/>. Between the GCM tag and the
/// SHA-256 verification, a blob can never silently decode to the wrong bytes.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MigratingBlobCodec : IBlobCodec
{
    private readonly IBlobCodec _plain;
    private readonly Func<IBlobCodec> _cipherFactory;
    private readonly object _cipherLock = new();
    private IBlobCodec? _cipher;
    private volatile bool _encryptNew;

    /// <param name="cipherFactory">
    /// Creates the AES codec on first use (loads or creates the DPAPI-sealed key). Deferred
    /// so a store that never enables encryption never creates a key file.
    /// </param>
    public MigratingBlobCodec(IBlobCodec plain, Func<IBlobCodec> cipherFactory, bool encryptNew)
    {
        _plain = plain;
        _cipherFactory = cipherFactory;
        _encryptNew = encryptNew;
        if (encryptNew)
        {
            _ = Cipher; // fail fast at construction if key material is unavailable
        }
    }

    public bool EncryptNew => _encryptNew;

    public string Id => _encryptNew ? "aesgcm-deflate" : "gzip";

    private IBlobCodec Cipher
    {
        get
        {
            if (_cipher is null)
            {
                lock (_cipherLock)
                {
                    _cipher ??= _cipherFactory();
                }
            }

            return _cipher;
        }
    }

    /// <summary>Flips the target format for newly written blobs (existing blobs stay readable).</summary>
    public void SetEncryptNew(bool encrypt)
    {
        if (encrypt)
        {
            _ = Cipher; // surface key-creation failures to the caller, not to a later capture
        }

        _encryptNew = encrypt;
    }

    public byte[] Encode(ReadOnlySpan<byte> plaintext)
        => _encryptNew ? Cipher.Encode(plaintext) : _plain.Encode(plaintext);

    public byte[] Decode(ReadOnlySpan<byte> stored)
        => _encryptNew ? Cipher.Decode(stored) : _plain.Decode(stored);

    /// <summary>
    /// Decodes with the OTHER format (used by <see cref="BlobStore.Get"/> when the primary
    /// decode fails or fails hash verification). Returns null when the other format is
    /// unavailable — e.g. an encrypted blob found while the cipher key can't be created.
    /// </summary>
    public byte[]? DecodeAlternate(ReadOnlySpan<byte> stored)
    {
        try
        {
            return _encryptNew ? _plain.Decode(stored) : Cipher.Decode(stored);
        }
        catch
        {
            return null;
        }
    }
}
