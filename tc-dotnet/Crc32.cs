using System.Buffers.Binary;

namespace TrueCryptReader;

/// <summary>
/// CRC-32 matching TrueCrypt's implementation (standard CRC-32/ISO-HDLC).
/// Polynomial: 0xEDB88320 (reflected 0x04C11DB7).
/// </summary>
public static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint crc = i;
            for (int j = 0; j < 8; j++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            table[i] = crc;
        }
        return table;
    }

    public static uint Compute(byte[] data, int offset, int length)
    {
        uint crc = 0xFFFFFFFF;
        for (int i = 0; i < length; i++)
            crc = Table[(crc ^ data[offset + i]) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        for (int i = 0; i < data.Length; i++)
            crc = Table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }
}
