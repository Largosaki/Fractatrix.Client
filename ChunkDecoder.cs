namespace Fractatrix.Client.Core;

using System.Buffers.Binary;
using System.Security.Cryptography;

/// <summary>
/// Decodes block storage formats received from the server.
/// Supports all 4 storage types: Uni(0), Rle(1), Plate(2), Flat(3).
/// Shared by ATDebug and FractatrixGodot.
/// </summary>
public static class ChunkDecoder
{
    /// <summary>Decode the block ID at the given flatIndex from chunk storage data.</summary>
    /// <returns>Block state ID, or -1 if format is unrecognised.</returns>
    public static int GetBlock(byte typeTag, byte[] data, int flatIndex)
    {
        return typeTag switch
        {
            0 => DecodeUniStorage(data),
            1 => DecodeRleStorage(data, flatIndex),
            2 => DecodePlateStorage(data, flatIndex),
            3 => DecodeFlatStorage(data, flatIndex),
            _ => -1,
        };
    }

    // TypeTag=0 UniStorage: 4 bytes LE int32 = single block ID for whole chunk
    private static int DecodeUniStorage(byte[] data)
    {
        if (data.Length < 4) return -1;
        return BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0, 4));
    }

    // TypeTag=1 RleStorage: [int32 runCount][runCount * (int32 stateId + int32 count)]
    private static int DecodeRleStorage(byte[] data, int flatIndex)
    {
        if (data.Length < 4) return -1;
        int runCount = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0, 4));
        int offset = 4;
        int current = 0;
        for (int i = 0; i < runCount; i++)
        {
            if (offset + 8 > data.Length) return -1;
            int stateId = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
            int count   = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset + 4, 4));
            offset += 8;
            current += count;
            if (flatIndex < current)
                return stateId;
        }
        return -1;
    }

    // TypeTag=2 PlateStorage: [uint16 paletteCount][paletteCount * int32 palette][32768 bytes indices]
    private static int DecodePlateStorage(byte[] data, int flatIndex)
    {
        if (data.Length < 2) return -1;
        int paletteCount = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(0, 2));
        int paletteStart = 2;
        int indicesStart = paletteStart + paletteCount * 4;
        if (data.Length < indicesStart + 32768) return -1;
        if (flatIndex < 0 || flatIndex >= 32768) return -1;
        byte paletteIndex = data[indicesStart + flatIndex];
        if (paletteIndex >= paletteCount) return -1;
        return BinaryPrimitives.ReadInt32LittleEndian(
            data.AsSpan(paletteStart + paletteIndex * 4, 4));
    }

    // TypeTag=3 FlatStorage: [32768 * int32 LE]
    private static int DecodeFlatStorage(byte[] data, int flatIndex)
    {
        if (data.Length < 32768 * 4) return -1;
        if (flatIndex < 0 || flatIndex >= 32768) return -1;
        return BinaryPrimitives.ReadInt32LittleEndian(
            data.AsSpan(flatIndex * 4, 4));
    }

    private const int BlockCount = 32768; // 32^3

    /// <summary>
    /// Decode all 32768 blocks from chunk storage data into an int[] array.
    /// Used by rendering clients that need the full block grid.
    /// Returns null if format is unrecognised or data is malformed.
    /// </summary>
    public static int[]? DecodeAll(byte typeTag, byte[] data)
    {
        return typeTag switch
        {
            0 => DecodeAllUni(data),
            1 => DecodeAllRle(data),
            2 => DecodeAllPlate(data),
            3 => DecodeAllFlat(data),
            _ => null,
        };
    }

    private static int[]? DecodeAllUni(byte[] data)
    {
        if (data.Length < 4) return null;
        int stateId = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0, 4));
        var blocks = new int[BlockCount];
        Array.Fill(blocks, stateId);
        return blocks;
    }

    private static int[]? DecodeAllRle(byte[] data)
    {
        if (data.Length < 4) return null;
        var span = data.AsSpan();
        int runCount = BinaryPrimitives.ReadInt32LittleEndian(span);
        var blocks = new int[BlockCount];
        int pos = 0, offset = 4;
        for (int r = 0; r < runCount; r++)
        {
            if (offset + 8 > data.Length) return null;
            int stateId = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset, 4));
            int count   = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset + 4, 4));
            offset += 8;
            Array.Fill(blocks, stateId, pos, count);
            pos += count;
        }
        return blocks;
    }

    private static int[]? DecodeAllPlate(byte[] data)
    {
        if (data.Length < 2) return null;
        var span = data.AsSpan();
        int paletteCount = BinaryPrimitives.ReadUInt16LittleEndian(span);
        int indicesStart = 2 + paletteCount * 4;
        if (data.Length < indicesStart + BlockCount) return null;
        var palette = new int[paletteCount];
        for (int i = 0; i < paletteCount; i++)
            palette[i] = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(2 + i * 4, 4));
        var blocks = new int[BlockCount];
        for (int i = 0; i < BlockCount; i++)
            blocks[i] = palette[span[indicesStart + i]];
        return blocks;
    }

    private static int[]? DecodeAllFlat(byte[] data)
    {
        if (data.Length < BlockCount * 4) return null;
        var span = data.AsSpan();
        var blocks = new int[BlockCount];
        for (int i = 0; i < BlockCount; i++)
            blocks[i] = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(i * 4, 4));
        return blocks;
    }

    /// <summary>Compute SHA256 of chunk data, returned as base64.</summary>
    public static string Sha256(byte[] data)
    {
        byte[] hash = SHA256.HashData(data);
        return Convert.ToBase64String(hash);
    }

    public const int ChunkSize = 32;

    /// <summary>World coord → chunk coord (floor division by 32) for one axis.</summary>
    public static int WorldToChunk(long w)
    {
        int q = (int)(w / ChunkSize);
        if ((w ^ ChunkSize) < 0 && w % ChunkSize != 0) q--;
        return q;
    }

    /// <summary>World coord → chunk coord for all three axes.</summary>
    public static (int cx, int cy, int cz) WorldToChunk(long wx, long wy, long wz) =>
        (WorldToChunk(wx), WorldToChunk(wy), WorldToChunk(wz));

    /// <summary>World coord → local coord within chunk (0..31) for one axis.</summary>
    public static int WorldToLocal(long w) => (int)(((w % ChunkSize) + ChunkSize) % ChunkSize);

    /// <summary>World coord → flat index within chunk. Layout: (ly &lt;&lt; 10) | (lz &lt;&lt; 5) | lx</summary>
    public static int WorldToFlatIndex(long wx, long wy, long wz)
    {
        int lx = (int)(((wx % 32) + 32) % 32);
        int ly = (int)(((wy % 32) + 32) % 32);
        int lz = (int)(((wz % 32) + 32) % 32);
        return (ly << 10) | (lz << 5) | lx;
    }
}
