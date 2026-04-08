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

    /// <summary>Compute SHA256 of chunk data, returned as base64.</summary>
    public static string Sha256(byte[] data)
    {
        byte[] hash = SHA256.HashData(data);
        return Convert.ToBase64String(hash);
    }

    /// <summary>World coord → chunk coord (floor division by 32).</summary>
    public static (int cx, int cy, int cz) WorldToChunk(long wx, long wy, long wz)
    {
        static int FloorDiv(long a, long b)
        {
            int q = (int)(a / b);
            if ((a ^ b) < 0 && a % b != 0) q--;
            return q;
        }
        return (FloorDiv(wx, 32), FloorDiv(wy, 32), FloorDiv(wz, 32));
    }

    /// <summary>World coord → flat index within chunk. Layout: (ly &lt;&lt; 10) | (lz &lt;&lt; 5) | lx</summary>
    public static int WorldToFlatIndex(long wx, long wy, long wz)
    {
        int lx = (int)(((wx % 32) + 32) % 32);
        int ly = (int)(((wy % 32) + 32) % 32);
        int lz = (int)(((wz % 32) + 32) % 32);
        return (ly << 10) | (lz << 5) | lx;
    }
}
