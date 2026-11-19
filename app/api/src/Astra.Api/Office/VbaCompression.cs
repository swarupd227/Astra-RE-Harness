using System.Buffers.Binary;

namespace Astra.Api.Office;

/// <summary>
/// The decompression half of [MS-OVBA] 2.4.1: how VBA stores module source and
/// the project's <c>dir</c> stream. A container is a signature byte (1) and a
/// run of chunks of at most 4096 decompressed bytes, each either raw or a mix of
/// literal bytes and copy tokens pointing back into the same chunk.
/// </summary>
public static class VbaCompression
{
    /// <summary>A decompressed source over this is refused rather than allocated.</summary>
    public const int MaxDecompressedBytes = 32 << 20;

    /// <exception cref="InvalidDataException">Not a compressed container, or a token points outside its chunk.</exception>
    public static byte[] Decompress(ReadOnlySpan<byte> data, int offset = 0)
    {
        if (offset < 0 || offset >= data.Length || data[offset] != 0x01)
            throw new InvalidDataException("no compressed container at this offset");

        var output = new List<byte>(Math.Min(MaxDecompressedBytes, (data.Length - offset) * 3));
        var pos = offset + 1;
        while (pos + 2 <= data.Length)
        {
            var header = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(pos, 2));
            var chunkSize = (header & 0x0FFF) + 3;
            if (((header >> 12) & 0x7) != 0b011) throw new InvalidDataException($"bad chunk signature at {pos}");
            var compressed = (header & 0x8000) != 0;
            var chunkEnd = Math.Min(data.Length, pos + chunkSize);
            pos += 2;
            var chunkStart = output.Count;

            if (!compressed)
            {
                var n = Math.Min(4096, data.Length - pos);
                for (var i = 0; i < n; i++) output.Add(data[pos + i]);
                pos += 4096;
            }
            else
            {
                while (pos < chunkEnd)
                {
                    var flags = data[pos++];
                    for (var bit = 0; bit < 8 && pos < chunkEnd; bit++)
                    {
                        if (((flags >> bit) & 1) == 0)
                        {
                            output.Add(data[pos++]);
                            continue;
                        }
                        if (pos + 2 > chunkEnd) throw new InvalidDataException("copy token cut off");
                        var token = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(pos, 2));
                        pos += 2;
                        var difference = output.Count - chunkStart;
                        var bitCount = 4;
                        while ((1 << bitCount) < difference && bitCount < 12) bitCount++;
                        var lengthMask = 0xFFFF >> bitCount;
                        var length = (token & lengthMask) + 3;
                        var back = ((token & ~lengthMask & 0xFFFF) >> (16 - bitCount)) + 1;
                        var src = output.Count - back;
                        if (src < chunkStart) throw new InvalidDataException("copy token points before its chunk");
                        for (var i = 0; i < length; i++) output.Add(output[src + i]);
                    }
                }
                pos = chunkEnd;
            }
            if (output.Count > MaxDecompressedBytes) throw new InvalidDataException("decompressed size limit exceeded");
        }
        return output.ToArray();
    }
}
