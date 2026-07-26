using System.IO.Compression;

namespace StepWind.Core.Storage;

/// <summary>
/// Default codec: Deflate compression, no encryption (ACL-protected store).
///
/// <see cref="CompressionLevel.Optimal"/>, not Fastest. Fastest is Deflate level 1 and it showed:
/// measured across a captured build it stored 81.7 MiB where Optimal stored 74.1 MiB, and on
/// document-like text the difference was 48.6 MiB versus 27.5 MiB — nearly half. Nothing about the
/// on-disk format changes, because a Deflate stream is self-describing: the level is an encoder
/// choice only, so every blob written at the old level still decodes with this decoder and no
/// migration or re-encoding is needed.
/// </summary>
public sealed class GzipBlobCodec : IBlobCodec
{
    public string Id => "deflate";

    public byte[] Encode(ReadOnlySpan<byte> plaintext)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(plaintext);
        }

        return output.ToArray();
    }

    public byte[] Decode(ReadOnlySpan<byte> stored)
    {
        using var input = new MemoryStream(stored.ToArray());
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        deflate.CopyTo(output);
        return output.ToArray();
    }
}
