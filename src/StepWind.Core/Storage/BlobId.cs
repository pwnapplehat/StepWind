using System.Security.Cryptography;

namespace StepWind.Core.Storage;

/// <summary>
/// Content address of a chunk: the SHA-256 of its plaintext bytes, lowercase hex. The store
/// is keyed by this, giving free deduplication (identical content → identical id → stored
/// once) and integrity (a blob that doesn't hash back to its id is corrupt). Hashing the
/// PLAINTEXT (not the ciphertext) keeps dedup working even with encryption on.
/// </summary>
public readonly record struct BlobId
{
    private BlobId(string hex) => Hex = hex;

    public string Hex { get; }

    public static BlobId OfContent(ReadOnlySpan<byte> content)
        => new(Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant());

    public static BlobId Parse(string hex)
    {
        if (hex.Length != 64 || !hex.All(Uri.IsHexDigit))
        {
            throw new FormatException($"Not a valid blob id: '{hex}'.");
        }

        return new BlobId(hex.ToLowerInvariant());
    }

    /// <summary>Sharded relative path ("ab/cdef…") so no directory holds millions of files.</summary>
    public string RelativePath => Path.Combine(Hex[..2], Hex[2..]);

    public override string ToString() => Hex;
}
