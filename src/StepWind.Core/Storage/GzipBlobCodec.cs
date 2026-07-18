using System.IO.Compression;

namespace StepWind.Core.Storage;

/// <summary>Default codec: Deflate compression, no encryption (ACL-protected store).</summary>
public sealed class GzipBlobCodec : IBlobCodec
{
    public string Id => "deflate";

    public byte[] Encode(ReadOnlySpan<byte> plaintext)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true))
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
