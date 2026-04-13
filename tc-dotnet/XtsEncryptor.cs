namespace TrueCryptReader;

/// <summary>
/// XTS (XEX-based Tweaked-codebook mode with ciphertext Stealing) encryptor.
/// Implements IEEE P1619 XTS-AES (generalized for any 128-bit block cipher).
/// Mirrors XtsDecryptor but performs encryption instead of decryption.
/// </summary>
public static class XtsEncryptor
{
    /// <summary>
    /// Encrypts data in XTS mode using a single cipher (no cascade).
    /// </summary>
    public static void EncryptXts(
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
            int endBlock = Math.Min(startBlockNo + blocksRemaining,
                                    TrueCryptConstants.BlocksPerXtsDataUnit);

            // Generate initial tweak: encrypt data unit number with secondary key
            byte[] tweak = new byte[16];
            tweak[0] = (byte)(dataUnitNo);
            tweak[1] = (byte)(dataUnitNo >> 8);
            tweak[2] = (byte)(dataUnitNo >> 16);
            tweak[3] = (byte)(dataUnitNo >> 24);
            tweak[4] = (byte)(dataUnitNo >> 32);
            tweak[5] = (byte)(dataUnitNo >> 40);
            tweak[6] = (byte)(dataUnitNo >> 48);
            tweak[7] = (byte)(dataUnitNo >> 56);

            secondary.EncryptBlock(tweak, 0);

            for (int i = 0; i < startBlockNo; i++)
                GfMul128(tweak);

            // Encrypt each block in this data unit
            for (int block = startBlockNo; block < endBlock; block++)
            {
                // XOR plaintext with tweak
                for (int i = 0; i < 16; i++)
                    data[pos + i] ^= tweak[i];

                // Encrypt block (uses EncryptBlock, not DecryptBlock)
                primary.EncryptBlock(data, pos);

                // XOR ciphertext with tweak
                for (int i = 0; i < 16; i++)
                    data[pos + i] ^= tweak[i];

                GfMul128(tweak);
                pos += 16;
            }

            blocksRemaining -= (endBlock - startBlockNo);
            startBlockNo = 0;
            dataUnitNo++;
        }
    }

    /// <summary>
    /// Encrypts data using a cascade of ciphers in XTS mode.
    /// For encryption, ciphers are applied in forward order (first cipher first),
    /// opposite of decryption which goes in reverse.
    /// </summary>
    public static void EncryptXtsCascade(
        byte[] data, int offset, int length,
        ulong dataUnitNo,
        CipherEngine[] primaryEngines, CipherEngine[] secondaryEngines)
    {
        // Encrypt in forward cipher order (opposite of decrypt)
        for (int i = 0; i < primaryEngines.Length; i++)
        {
            EncryptXts(data, offset, length, dataUnitNo, 0,
                       primaryEngines[i], secondaryEngines[i]);
        }
    }

    private static void GfMul128(byte[] tweak)
    {
        bool carry = (tweak[15] & 0x80) != 0;
        for (int i = 15; i > 0; i--)
            tweak[i] = (byte)((tweak[i] << 1) | (tweak[i - 1] >> 7));
        tweak[0] = (byte)(tweak[0] << 1);
        if (carry)
            tweak[0] ^= 0x87;
    }
}
