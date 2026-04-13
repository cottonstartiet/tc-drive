using System.Buffers.Binary;

namespace TrueCryptReader;

/// <summary>
/// Parsed and validated TrueCrypt volume header.
/// </summary>
public class VolumeHeader
{
    public ushort HeaderVersion { get; init; }
    public ushort RequiredProgramVersion { get; init; }
    public ulong HiddenVolumeSize { get; init; }
    public ulong VolumeSize { get; init; }
    public ulong EncryptedAreaStart { get; init; }
    public ulong EncryptedAreaLength { get; init; }
    public uint Flags { get; init; }
    public uint SectorSize { get; init; }
    public byte[] MasterKeyData { get; init; } = null!;
    public byte[] Salt { get; init; } = null!;

    public EncryptionAlgorithm EncryptionAlgorithm { get; init; } = null!;
    public TrueCryptPrf Prf { get; init; }
    public bool IsHiddenVolume => HiddenVolumeSize != 0;

    /// <summary>
    /// Attempts to decrypt and validate the volume header.
    /// Tries all PRF × cipher combinations. Returns null if password is wrong.
    /// </summary>
    public static VolumeHeader? TryDecrypt(byte[] headerBytes, byte[] password, bool boot = false,
        Action<int, int, string>? onAttempt = null)
    {
        if (headerBytes.Length < TrueCryptConstants.VolumeHeaderEffectiveSize)
            throw new ArgumentException("Header must be at least 512 bytes.");

        // Extract salt (unencrypted, first 64 bytes)
        byte[] salt = new byte[TrueCryptConstants.SaltSize];
        Buffer.BlockCopy(headerBytes, TrueCryptConstants.HeaderSaltOffset, salt, 0, TrueCryptConstants.SaltSize);

        int totalCombos = KeyDerivation.AllPrfs.Length * EncryptionAlgorithm.All.Length;
        int currentCombo = 0;

        // Try each PRF
        foreach (var prf in KeyDerivation.AllPrfs)
        {
            byte[] derivedKey;
            try
            {
                derivedKey = KeyDerivation.DeriveKey(password, salt, prf, boot);
            }
            catch
            {
                continue;
            }

            // Try each encryption algorithm (XTS mode only for modern volumes)
            foreach (var ea in EncryptionAlgorithm.All)
            {
                currentCombo++;
                onAttempt?.Invoke(currentCombo, totalCombos, $"{prf} + {ea.Name}");

                // Need 2× key size (primary + secondary XTS keys)
                if (ea.KeySize * 2 > derivedKey.Length)
                    continue;

                var (primaryEngines, secondaryEngines) = ea.CreateEngines(derivedKey);

                // Make a copy of the encrypted header portion for decryption attempt
                byte[] header = new byte[TrueCryptConstants.VolumeHeaderEffectiveSize];
                Buffer.BlockCopy(headerBytes, 0, header, 0, header.Length);

                // Decrypt bytes 64..511 (encrypted portion) using XTS with data unit #0
                XtsDecryptor.DecryptXtsCascade(
                    header,
                    TrueCryptConstants.HeaderEncryptedDataOffset,
                    TrueCryptConstants.HeaderEncryptedDataSize,
                    dataUnitNo: 0,
                    primaryEngines, secondaryEngines);

                // Check magic "TRUE" at offset 64
                uint magic = ReadBigEndianUInt32(header, TrueCryptConstants.OffsetMagic);
                if (magic != TrueCryptConstants.MagicTrue)
                    continue;

                // Validate header CRC (CRC32 of bytes 64..251 vs value at offset 252)
                uint headerCrc = ReadBigEndianUInt32(header, TrueCryptConstants.OffsetHeaderCrc);
                uint computedHeaderCrc = Crc32.Compute(header,
                    TrueCryptConstants.OffsetMagic,
                    TrueCryptConstants.OffsetHeaderCrc - TrueCryptConstants.OffsetMagic);
                if (headerCrc != computedHeaderCrc)
                    continue;

                // Validate key area CRC (CRC32 of bytes 256..511 vs value at offset 72)
                uint keyAreaCrc = ReadBigEndianUInt32(header, TrueCryptConstants.OffsetKeyAreaCrc);
                uint computedKeyAreaCrc = Crc32.Compute(header,
                    TrueCryptConstants.HeaderMasterKeyDataOffset,
                    TrueCryptConstants.MasterKeyDataSize);
                if (keyAreaCrc != computedKeyAreaCrc)
                    continue;

                // Success! Parse header fields
                ushort version = ReadBigEndianUInt16(header, TrueCryptConstants.OffsetVersion);
                uint sectorSize = version >= 5
                    ? ReadBigEndianUInt32(header, TrueCryptConstants.OffsetSectorSize)
                    : 512u;

                byte[] masterKeyData = new byte[TrueCryptConstants.MasterKeyDataSize];
                Buffer.BlockCopy(header, TrueCryptConstants.HeaderMasterKeyDataOffset,
                    masterKeyData, 0, TrueCryptConstants.MasterKeyDataSize);

                return new VolumeHeader
                {
                    HeaderVersion = version,
                    RequiredProgramVersion = ReadBigEndianUInt16(header, TrueCryptConstants.OffsetRequiredVersion),
                    HiddenVolumeSize = ReadBigEndianUInt64(header, TrueCryptConstants.OffsetHiddenVolumeSize),
                    VolumeSize = ReadBigEndianUInt64(header, TrueCryptConstants.OffsetVolumeSize),
                    EncryptedAreaStart = ReadBigEndianUInt64(header, TrueCryptConstants.OffsetEncryptedAreaStart),
                    EncryptedAreaLength = ReadBigEndianUInt64(header, TrueCryptConstants.OffsetEncryptedAreaLength),
                    Flags = ReadBigEndianUInt32(header, TrueCryptConstants.OffsetFlags),
                    SectorSize = sectorSize,
                    MasterKeyData = masterKeyData,
                    Salt = salt,
                    EncryptionAlgorithm = ea,
                    Prf = prf,
                };
            }
        }

        return null; // Wrong password or unsupported format
    }

    private static ushort ReadBigEndianUInt16(byte[] data, int offset) =>
        BinaryPrimitives.ReverseEndianness(BitConverter.ToUInt16(data, offset));

    private static uint ReadBigEndianUInt32(byte[] data, int offset) =>
        BinaryPrimitives.ReverseEndianness(BitConverter.ToUInt32(data, offset));

    private static ulong ReadBigEndianUInt64(byte[] data, int offset) =>
        BinaryPrimitives.ReverseEndianness(BitConverter.ToUInt64(data, offset));
}
