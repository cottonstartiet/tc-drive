namespace TrueCryptReader;

/// <summary>
/// Constants matching TrueCrypt 7.1a source (Crypto.h, Volumes.h, Password.h).
/// </summary>
public static class TrueCryptConstants
{
    // Volume header sizes
    public const int VolumeHeaderSize = 64 * 1024;           // TC_VOLUME_HEADER_SIZE
    public const int VolumeHeaderEffectiveSize = 512;        // TC_VOLUME_HEADER_EFFECTIVE_SIZE
    public const int VolumeHeaderGroupSize = 2 * VolumeHeaderSize; // TC_VOLUME_HEADER_GROUP_SIZE
    public const long VolumeDataOffset = VolumeHeaderGroupSize;    // TC_VOLUME_DATA_OFFSET = 128KB

    // Hidden volume header offset
    public const long HiddenVolumeHeaderOffset = VolumeHeaderSize; // 64KB

    // Salt
    public const int SaltSize = 64;                          // PKCS5_SALT_SIZE

    // Master key data
    public const int MasterKeyDataSize = 256;                // MASTER_KEYDATA_SIZE

    // Encryption data unit
    public const int EncryptionDataUnitSize = 512;           // ENCRYPTION_DATA_UNIT_SIZE
    public const int BytesPerXtsBlock = 16;                  // BYTES_PER_XTS_BLOCK
    public const int BlocksPerXtsDataUnit = EncryptionDataUnitSize / BytesPerXtsBlock; // 32

    // Header field offsets (within the 512-byte header)
    public const int HeaderSaltOffset = 0;
    public const int HeaderEncryptedDataOffset = SaltSize;   // 64
    public const int HeaderMasterKeyDataOffset = 256;
    public const int HeaderEncryptedDataSize = VolumeHeaderEffectiveSize - HeaderEncryptedDataOffset; // 448

    public const int OffsetMagic = 64;
    public const int OffsetVersion = 68;
    public const int OffsetRequiredVersion = 70;
    public const int OffsetKeyAreaCrc = 72;
    public const int OffsetHiddenVolumeSize = 92;
    public const int OffsetVolumeSize = 100;
    public const int OffsetEncryptedAreaStart = 108;
    public const int OffsetEncryptedAreaLength = 116;
    public const int OffsetFlags = 124;
    public const int OffsetSectorSize = 128;
    public const int OffsetHeaderCrc = 252;

    // Magic value "TRUE"
    public const uint MagicTrue = 0x54525545;

    // Password limits
    public const int MaxPassword = 64;

    // Legacy IV size (for LRW/CBC modes)
    public const int LegacyVolIvSize = 32;
}
