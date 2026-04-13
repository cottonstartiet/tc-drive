using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace TrueCryptReader;

/// <summary>
/// Creates new TrueCrypt-compatible encrypted volumes.
/// Uses AES-256 encryption with PBKDF2-HMAC-SHA512 key derivation.
/// Header format: TrueCrypt v5 (compatible with TrueCrypt 7.x).
/// </summary>
public static class VolumeCreator
{
    private static readonly EncryptionAlgorithm DefaultAlgorithm = EncryptionAlgorithm.All[0]; // AES
    private const TrueCryptPrf DefaultPrf = TrueCryptPrf.Sha512;
    private const ushort HeaderVersion = 0x0005;
    private const ushort RequiredProgramVersion = 0x0700;

    /// <summary>
    /// Creates a new TrueCrypt volume with NTFS filesystem.
    /// </summary>
    /// <param name="volumePath">Path for the new volume file.</param>
    /// <param name="sizeBytes">Total volume file size in bytes (including headers).</param>
    /// <param name="password">Encryption password.</param>
    /// <param name="progress">Optional progress callback (0.0 to 1.0).</param>
    public static void Create(string volumePath, long sizeBytes, string password, Action<double>? progress = null)
    {
        if (File.Exists(volumePath))
            throw new IOException($"Volume file already exists: {volumePath}");

        // Minimum size: headers (256KB) + at least 1MB data area
        long minSize = TrueCryptConstants.VolumeHeaderGroupSize * 2 + 1024 * 1024;
        if (sizeBytes < minSize)
            throw new ArgumentException($"Volume size must be at least {minSize / 1024}KB.");

        byte[] passwordBytes = Encoding.UTF8.GetBytes(password);

        // Data area = total size - header space (128KB front + 128KB back)
        long dataAreaSize = sizeBytes - TrueCryptConstants.VolumeHeaderGroupSize * 2;
        long dataAreaStart = TrueCryptConstants.VolumeDataOffset; // 128KB

        // Generate cryptographic random data
        byte[] salt = RandomNumberGenerator.GetBytes(TrueCryptConstants.SaltSize);
        byte[] masterKeyData = RandomNumberGenerator.GetBytes(TrueCryptConstants.MasterKeyDataSize);

        // Derive header encryption key
        byte[] derivedKey = KeyDerivation.DeriveKey(passwordBytes, salt, DefaultPrf, boot: false);

        progress?.Invoke(0.1);

        // Build the 512-byte header
        byte[] header = BuildHeader(salt, masterKeyData, (ulong)dataAreaSize,
            (ulong)dataAreaStart, (ulong)dataAreaSize);

        // Encrypt header bytes 64..511 using XTS with derived key
        EncryptHeader(header, derivedKey);

        progress?.Invoke(0.2);

        // Create cipher engines from master key for data area encryption
        var (primaryEngines, secondaryEngines) = DefaultAlgorithm.CreateEngines(masterKeyData);

        // Create the volume file
        using var fileStream = new FileStream(volumePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);

        // Preallocate file
        fileStream.SetLength(sizeBytes);

        // 1. Write primary header (512 bytes at offset 0, rest of 64KB is random)
        fileStream.Seek(0, SeekOrigin.Begin);
        fileStream.Write(header, 0, TrueCryptConstants.VolumeHeaderEffectiveSize);

        // Fill rest of primary header area with random data
        byte[] randPad = RandomNumberGenerator.GetBytes(
            TrueCryptConstants.VolumeHeaderSize - TrueCryptConstants.VolumeHeaderEffectiveSize);
        fileStream.Write(randPad, 0, randPad.Length);

        // 2. Write hidden volume header area (64KB random data for plausible deniability)
        byte[] hiddenArea = RandomNumberGenerator.GetBytes(TrueCryptConstants.VolumeHeaderSize);
        fileStream.Write(hiddenArea, 0, hiddenArea.Length);

        progress?.Invoke(0.3);

        // 3. Create NTFS filesystem and encrypt it into data area
        WriteEncryptedNtfsDataArea(fileStream, dataAreaStart, dataAreaSize,
            primaryEngines, secondaryEngines, progress);

        progress?.Invoke(0.9);

        // 4. Write backup headers at end of volume
        // Backup primary header
        byte[] backupSalt = RandomNumberGenerator.GetBytes(TrueCryptConstants.SaltSize);
        byte[] backupDerivedKey = KeyDerivation.DeriveKey(passwordBytes, backupSalt, DefaultPrf, boot: false);
        byte[] backupHeader = BuildHeader(backupSalt, masterKeyData, (ulong)dataAreaSize,
            (ulong)dataAreaStart, (ulong)dataAreaSize);
        EncryptHeader(backupHeader, backupDerivedKey);

        long backupOffset = dataAreaStart + dataAreaSize;
        fileStream.Seek(backupOffset, SeekOrigin.Begin);
        fileStream.Write(backupHeader, 0, TrueCryptConstants.VolumeHeaderEffectiveSize);

        // Fill rest of backup primary header with random
        randPad = RandomNumberGenerator.GetBytes(
            TrueCryptConstants.VolumeHeaderSize - TrueCryptConstants.VolumeHeaderEffectiveSize);
        fileStream.Write(randPad, 0, randPad.Length);

        // Backup hidden header (random)
        byte[] backupHidden = RandomNumberGenerator.GetBytes(TrueCryptConstants.VolumeHeaderSize);
        fileStream.Write(backupHidden, 0, backupHidden.Length);

        fileStream.Flush();
        progress?.Invoke(1.0);
    }

    private static byte[] BuildHeader(byte[] salt, byte[] masterKeyData,
        ulong volumeSize, ulong encryptedAreaStart, ulong encryptedAreaLength)
    {
        byte[] header = new byte[TrueCryptConstants.VolumeHeaderEffectiveSize];

        // Salt (bytes 0-63) — plaintext
        Buffer.BlockCopy(salt, 0, header, TrueCryptConstants.HeaderSaltOffset, TrueCryptConstants.SaltSize);

        // Master key data (bytes 256-511) — will be encrypted
        Buffer.BlockCopy(masterKeyData, 0, header, TrueCryptConstants.HeaderMasterKeyDataOffset,
            TrueCryptConstants.MasterKeyDataSize);

        // CRC32 of master key data (offset 72)
        uint keyAreaCrc = Crc32.Compute(header, TrueCryptConstants.HeaderMasterKeyDataOffset,
            TrueCryptConstants.MasterKeyDataSize);

        // Magic "TRUE" (offset 64)
        WriteBigEndianUInt32(header, TrueCryptConstants.OffsetMagic, TrueCryptConstants.MagicTrue);

        // Header version (offset 68)
        WriteBigEndianUInt16(header, TrueCryptConstants.OffsetVersion, HeaderVersion);

        // Required program version (offset 70)
        WriteBigEndianUInt16(header, TrueCryptConstants.OffsetRequiredVersion, RequiredProgramVersion);

        // Key area CRC (offset 72)
        WriteBigEndianUInt32(header, TrueCryptConstants.OffsetKeyAreaCrc, keyAreaCrc);

        // Hidden volume size (offset 92) = 0 for standard volume
        WriteBigEndianUInt64(header, TrueCryptConstants.OffsetHiddenVolumeSize, 0);

        // Volume size (offset 100)
        WriteBigEndianUInt64(header, TrueCryptConstants.OffsetVolumeSize, volumeSize);

        // Encrypted area start (offset 108)
        WriteBigEndianUInt64(header, TrueCryptConstants.OffsetEncryptedAreaStart, encryptedAreaStart);

        // Encrypted area length (offset 116)
        WriteBigEndianUInt64(header, TrueCryptConstants.OffsetEncryptedAreaLength, encryptedAreaLength);

        // Flags (offset 124) = 0
        WriteBigEndianUInt32(header, TrueCryptConstants.OffsetFlags, 0);

        // Sector size (offset 128) = 512
        WriteBigEndianUInt32(header, TrueCryptConstants.OffsetSectorSize, 512);

        // Header CRC (offset 252) = CRC32 of bytes 64..251
        uint headerCrc = Crc32.Compute(header, TrueCryptConstants.OffsetMagic,
            TrueCryptConstants.OffsetHeaderCrc - TrueCryptConstants.OffsetMagic);
        WriteBigEndianUInt32(header, TrueCryptConstants.OffsetHeaderCrc, headerCrc);

        return header;
    }

    private static void EncryptHeader(byte[] header, byte[] derivedKey)
    {
        var (primaryEngines, secondaryEngines) = DefaultAlgorithm.CreateEngines(derivedKey);

        // Encrypt bytes 64..511 (448 bytes) using XTS with data unit #0
        XtsEncryptor.EncryptXtsCascade(
            header,
            TrueCryptConstants.HeaderEncryptedDataOffset,
            TrueCryptConstants.HeaderEncryptedDataSize,
            dataUnitNo: 0,
            primaryEngines, secondaryEngines);
    }

    private static void WriteEncryptedNtfsDataArea(FileStream fileStream,
        long dataAreaStart, long dataAreaSize,
        CipherEngine[] primaryEngines, CipherEngine[] secondaryEngines,
        Action<double>? progress)
    {
        // Create an in-memory NTFS filesystem using DiscUtils
        // For large volumes, we stream it in chunks
        byte[] buffer = new byte[TrueCryptConstants.EncryptionDataUnitSize];

        // Format NTFS using DiscUtils into a temp file to avoid memory issues
        string tempPath = Path.GetTempFileName();
        try
        {
            using (var tempStream = new FileStream(tempPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                tempStream.SetLength(dataAreaSize);

                // Use DiscUtils to format NTFS
                var ntfs = DiscUtils.Ntfs.NtfsFileSystem.Format(tempStream, "TrueCrypt", DiscUtils.Geometry.FromCapacity(dataAreaSize), 0, dataAreaSize);
                ntfs.Dispose();
            }

            // Read NTFS data, encrypt sector by sector, write to volume
            using var tempRead = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.None);
            fileStream.Seek(dataAreaStart, SeekOrigin.Begin);

            long totalSectors = dataAreaSize / TrueCryptConstants.EncryptionDataUnitSize;
            long sectorIndex = 0;

            // Use larger buffer for efficiency (64KB = 128 sectors)
            int bulkSize = 64 * 1024;
            byte[] bulkBuffer = new byte[bulkSize];

            while (sectorIndex < totalSectors)
            {
                int sectorsToProcess = (int)Math.Min(bulkSize / TrueCryptConstants.EncryptionDataUnitSize,
                    totalSectors - sectorIndex);
                int bytesToProcess = sectorsToProcess * TrueCryptConstants.EncryptionDataUnitSize;

                int bytesRead = ReadFull(tempRead, bulkBuffer, 0, bytesToProcess);
                if (bytesRead < bytesToProcess)
                    Array.Clear(bulkBuffer, bytesRead, bytesToProcess - bytesRead);

                // Encrypt each sector
                for (int i = 0; i < sectorsToProcess; i++)
                {
                    int sectorOffset = i * TrueCryptConstants.EncryptionDataUnitSize;
                    long fileOffset = dataAreaStart + (sectorIndex + i) * TrueCryptConstants.EncryptionDataUnitSize;
                    ulong dataUnitNo = (ulong)(fileOffset / TrueCryptConstants.EncryptionDataUnitSize);

                    XtsEncryptor.EncryptXtsCascade(bulkBuffer, sectorOffset,
                        TrueCryptConstants.EncryptionDataUnitSize, dataUnitNo,
                        primaryEngines, secondaryEngines);
                }

                fileStream.Write(bulkBuffer, 0, bytesToProcess);
                sectorIndex += sectorsToProcess;

                if (progress != null && totalSectors > 0)
                    progress(0.3 + 0.6 * ((double)sectorIndex / totalSectors));
            }
        }
        finally
        {
            try { File.Delete(tempPath); } catch { }
        }
    }

    private static int ReadFull(Stream stream, byte[] buffer, int offset, int count)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            int n = stream.Read(buffer, offset + totalRead, count - totalRead);
            if (n == 0) break;
            totalRead += n;
        }
        return totalRead;
    }

    private static void WriteBigEndianUInt16(byte[] data, int offset, ushort value)
    {
        ushort be = BinaryPrimitives.ReverseEndianness(value);
        BitConverter.TryWriteBytes(data.AsSpan(offset), be);
    }

    private static void WriteBigEndianUInt32(byte[] data, int offset, uint value)
    {
        uint be = BinaryPrimitives.ReverseEndianness(value);
        BitConverter.TryWriteBytes(data.AsSpan(offset), be);
    }

    private static void WriteBigEndianUInt64(byte[] data, int offset, ulong value)
    {
        ulong be = BinaryPrimitives.ReverseEndianness(value);
        BitConverter.TryWriteBytes(data.AsSpan(offset), be);
    }
}
