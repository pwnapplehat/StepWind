namespace StepWind.Core.Storage;

/// <summary>
/// Transforms a chunk's plaintext into the bytes actually written to disk, and back.
/// Two implementations: plain compression, and passphrase encryption. The blob id is always
/// the hash of the PLAINTEXT (computed by the store, not the codec), so switching codecs
/// never breaks deduplication or lookups.
/// </summary>
public interface IBlobCodec
{
    /// <summary>Codec tag persisted in the repo config so the store self-describes on open.</summary>
    string Id { get; }

    /// <summary>Plaintext chunk → on-disk bytes.</summary>
    byte[] Encode(ReadOnlySpan<byte> plaintext);

    /// <summary>On-disk bytes → plaintext chunk.</summary>
    byte[] Decode(ReadOnlySpan<byte> stored);
}
