namespace TrueCryptReader;

/// <summary>
/// XTS (XEX-based Tweaked-codebook mode with ciphertext Stealing) decryptor.
/// Implements IEEE P1619 XTS-AES (generalized for any 128-bit block cipher).
/// Matches TrueCrypt's Xts.c implementation.
/// </summary>
public static class XtsDecryptor
{
    /// <summary>
    /// Decrypts data in XTS mode using a single cipher (no cascade).
    /// </summary>
    /// <param name="data">Buffer to decrypt in-place. Must be a multiple of 16 bytes.</param>
    /// <param name="offset">Start offset in buffer.</param>
    /// <param name="length">Number of bytes to decrypt. Must be multiple of 16.</param>
    /// <param name="dataUnitNo">Starting data unit (sector) number.</param>
    /// <param name="startBlockNo">Starting block number within the first data unit (usually 0).</param>
    /// <param name="primary">Primary cipher engine (for data).</param>
    /// <param name="secondary">Secondary cipher engine (for tweak).</param>
    public static void DecryptXts(
        byte[] data, int offset, int length,
        ulong dataUnitNo, int startBlockNo,
        CipherEngine primary, CipherEngine secondary)
    {
        if (length % TrueCryptConstants.BytesPerXtsBlock != 0)
            throw new ArgumentException("Length must be a multiple of 16 bytes.");

        int blocksRemaining = length / TrueCryptConstants.BytesPerXtsBlock;
        int pos = offset;

        while (blocksRemaining > 0)
        {
            // How many blocks in this data unit?
            int endBlock = Math.Min(startBlockNo + blocksRemaining,
                                    TrueCryptConstants.BlocksPerXtsDataUnit);

            // Generate initial tweak: encrypt data unit number with secondary key
            byte[] tweak = new byte[16];
            // Data unit number in little-endian (lower 8 bytes), upper 8 bytes zero
            tweak[0] = (byte)(dataUnitNo);
            tweak[1] = (byte)(dataUnitNo >> 8);
            tweak[2] = (byte)(dataUnitNo >> 16);
            tweak[3] = (byte)(dataUnitNo >> 24);
            tweak[4] = (byte)(dataUnitNo >> 32);
            tweak[5] = (byte)(dataUnitNo >> 40);
            tweak[6] = (byte)(dataUnitNo >> 48);
            tweak[7] = (byte)(dataUnitNo >> 56);
            // bytes 8-15 already zero

            secondary.EncryptBlock(tweak, 0);

            // Advance tweak to startBlockNo (skip blocks before our start)
            for (int i = 0; i < startBlockNo; i++)
                GfMul128(tweak);

            // Decrypt each block in this data unit
            for (int block = startBlockNo; block < endBlock; block++)
            {
                // XOR plaintext with tweak
                for (int i = 0; i < 16; i++)
                    data[pos + i] ^= tweak[i];

                // Decrypt block
                primary.DecryptBlock(data, pos);

                // XOR ciphertext with tweak
                for (int i = 0; i < 16; i++)
                    data[pos + i] ^= tweak[i];

                // Advance tweak: multiply by α in GF(2^128)
                GfMul128(tweak);

                pos += 16;
            }

            blocksRemaining -= (endBlock - startBlockNo);
            startBlockNo = 0;
            dataUnitNo++;
        }
    }

    /// <summary>
    /// Decrypts data using a cascade of ciphers in XTS mode.
    /// For decryption, ciphers are applied in reverse order (last cipher first),
    /// each doing a full XTS pass over the data.
    /// Matches TrueCrypt's DecryptBuffer / DecryptDataUnitsCurrentThread in Crypto.c.
    /// </summary>
    public static void DecryptXtsCascade(
        byte[] data, int offset, int length,
        ulong dataUnitNo,
        CipherEngine[] primaryEngines, CipherEngine[] secondaryEngines)
    {
        // Decrypt in reverse cipher order (matching TrueCrypt's EAGetLastCipher → EAGetPreviousCipher)
        for (int i = primaryEngines.Length - 1; i >= 0; i--)
        {
            DecryptXts(data, offset, length, dataUnitNo, 0,
                       primaryEngines[i], secondaryEngines[i]);
        }
    }

    /// <summary>
    /// Multiply tweak by α (= x) in GF(2^128) with polynomial x^128+x^7+x^2+x+1.
    /// This is a left-shift-by-1 with conditional XOR of 0x87 into byte[0].
    /// Little-endian byte order.
    /// </summary>
    private static void GfMul128(byte[] tweak)
    {
        bool carry = (tweak[15] & 0x80) != 0;
        for (int i = 15; i > 0; i--)
            tweak[i] = (byte)((tweak[i] << 1) | (tweak[i - 1] >> 7));
        tweak[0] = (byte)(tweak[0] << 1);
        if (carry)
            tweak[0] ^= 0x87; // Reduction polynomial: x^128 + x^7 + x^2 + x + 1
    }
}
