using System.Text;
using DiscUtils;
using DiscUtils.Ext;
using DiscUtils.Fat;
using DiscUtils.Ntfs;

namespace TrueCryptReader;

/// <summary>
/// High-level API for opening TrueCrypt volumes and accessing files.
/// </summary>
public class TrueCryptVolume : IDisposable
{
    private readonly FileStream _fileStream;
    private readonly DecryptedVolumeStream _decryptedStream;
    private bool _disposed;

    public VolumeHeader Header { get; }

    private TrueCryptVolume(FileStream fileStream, DecryptedVolumeStream decryptedStream, VolumeHeader header)
    {
        _fileStream = fileStream;
        _decryptedStream = decryptedStream;
        Header = header;
    }

    /// <summary>
    /// Opens a TrueCrypt volume file with the given password.
    /// Tries the primary header first, then the hidden volume header.
    /// </summary>
    public static TrueCryptVolume Open(string volumePath, string password, bool writable = false,
        Action<string>? statusCallback = null)
    {
        byte[] passwordBytes = Encoding.UTF8.GetBytes(password);
        var fileStream = new FileStream(volumePath,
            FileMode.Open,
            writable ? FileAccess.ReadWrite : FileAccess.Read,
            writable ? FileShare.None : FileShare.Read);

        try
        {
            byte[] headerBytes = new byte[TrueCryptConstants.VolumeHeaderEffectiveSize];

            // Try decrypting primary header
            statusCallback?.Invoke("Trying primary header...");
            fileStream.Seek(0, SeekOrigin.Begin);
            ReadFull(fileStream, headerBytes, 0, headerBytes.Length);
            var header = VolumeHeader.TryDecrypt(headerBytes, passwordBytes,
                onAttempt: (cur, tot, desc) => statusCallback?.Invoke($"Primary header: {desc} ({cur}/{tot})"));

            // If primary fails, try hidden volume header at offset 64KB
            if (header == null)
            {
                statusCallback?.Invoke("Trying hidden volume header...");
                fileStream.Seek(TrueCryptConstants.HiddenVolumeHeaderOffset, SeekOrigin.Begin);
                ReadFull(fileStream, headerBytes, 0, headerBytes.Length);
                header = VolumeHeader.TryDecrypt(headerBytes, passwordBytes,
                    onAttempt: (cur, tot, desc) => statusCallback?.Invoke($"Hidden header: {desc} ({cur}/{tot})"));
            }

            // If still null, try backup header at end of volume
            if (header == null)
            {
                long backupOffset = fileStream.Length - TrueCryptConstants.VolumeHeaderGroupSize;
                if (backupOffset > 0)
                {
                    statusCallback?.Invoke("Trying backup header...");
                    fileStream.Seek(backupOffset, SeekOrigin.Begin);
                    ReadFull(fileStream, headerBytes, 0, headerBytes.Length);
                    header = VolumeHeader.TryDecrypt(headerBytes, passwordBytes,
                        onAttempt: (cur, tot, desc) => statusCallback?.Invoke($"Backup header: {desc} ({cur}/{tot})"));
                }
            }

            if (header == null)
                throw new InvalidPasswordException("Wrong password or not a TrueCrypt volume.");

            var decryptedStream = new DecryptedVolumeStream(fileStream, header, writable);
            return new TrueCryptVolume(fileStream, decryptedStream, header);
        }
        catch
        {
            fileStream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens the decrypted volume as a filesystem (FAT or NTFS) using DiscUtils.
    /// </summary>
    public DiscFileSystem OpenFileSystem()
    {
        _decryptedStream.Seek(0, SeekOrigin.Begin);

        // Read the first sector to detect filesystem type from OEM ID
        byte[] bootSector = new byte[512];
        int read = _decryptedStream.Read(bootSector, 0, bootSector.Length);
        _decryptedStream.Seek(0, SeekOrigin.Begin);

        string oemId = read >= 11
            ? System.Text.Encoding.ASCII.GetString(bootSector, 3, 8).Trim('\0', ' ')
            : "";

        Exception? fatEx = null;
        Exception? ntfsEx = null;
        Exception? extEx = null;

        // Try NTFS first if OEM ID says NTFS
        if (oemId.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var ntfs = new NtfsFileSystem(_decryptedStream);
                return ntfs;
            }
            catch (Exception ex) { ntfsEx = ex; }
        }

        // Try FAT
        _decryptedStream.Seek(0, SeekOrigin.Begin);
        try
        {
            var fat = new FatFileSystem(_decryptedStream);
            return fat;
        }
        catch (Exception ex) { fatEx = ex; }

        // Try NTFS (if not already tried)
        if (ntfsEx == null)
        {
            _decryptedStream.Seek(0, SeekOrigin.Begin);
            try
            {
                var ntfs = new NtfsFileSystem(_decryptedStream);
                return ntfs;
            }
            catch (Exception ex) { ntfsEx = ex; }
        }

        // Try ext2/3/4
        _decryptedStream.Seek(0, SeekOrigin.Begin);
        try
        {
            var ext = new ExtFileSystem(_decryptedStream);
            return ext;
        }
        catch (Exception ex) { extEx = ex; }

        // Dump diagnostic info
        Console.Error.WriteLine();
        Console.Error.WriteLine("=== FILESYSTEM DETECTION FAILED ===");
        Console.Error.WriteLine($"  FAT error:  {fatEx?.Message}");
        Console.Error.WriteLine($"  NTFS error: {ntfsEx?.Message}");
        Console.Error.WriteLine($"  ext error:  {extEx?.Message}");
        Console.Error.WriteLine();
        DumpFirstSector();

        throw new NotSupportedException(
            "Could not detect filesystem. Volume may use an unsupported filesystem type. " +
            "See diagnostic output above.");
    }

    /// <summary>
    /// Dumps the first 512 decrypted bytes as hex for diagnostics.
    /// </summary>
    public void DumpFirstSector()
    {
        _decryptedStream.Seek(0, SeekOrigin.Begin);
        byte[] sector = new byte[512];
        int read = _decryptedStream.Read(sector, 0, sector.Length);
        _decryptedStream.Seek(0, SeekOrigin.Begin);

        Console.Error.WriteLine($"=== FIRST DECRYPTED SECTOR ({read} bytes) ===");
        Console.Error.WriteLine($"  Stream length: {_decryptedStream.Length}");
        Console.Error.WriteLine($"  Data area offset: {_decryptedStream.DataAreaOffset}");
        Console.Error.WriteLine();

        for (int row = 0; row < Math.Min(read, 256); row += 16)
        {
            Console.Error.Write($"  {row:X4}: ");
            for (int col = 0; col < 16 && row + col < read; col++)
                Console.Error.Write($"{sector[row + col]:X2} ");

            Console.Error.Write("  ");
            for (int col = 0; col < 16 && row + col < read; col++)
            {
                byte b = sector[row + col];
                Console.Error.Write(b >= 32 && b < 127 ? (char)b : '.');
            }
            Console.Error.WriteLine();
        }
        Console.Error.WriteLine();

        // Check common filesystem signatures
        if (read >= 4)
        {
            if (sector[0] == 0xEB || sector[0] == 0xE9)
                Console.Error.WriteLine("  Signature: Looks like a FAT/NTFS boot sector (jump instruction found)");
            else if (sector[0] == 0x00 && sector[1] == 0x00)
                Console.Error.WriteLine("  Signature: First bytes are zeros — decryption may have failed or volume is empty");
            else
                Console.Error.WriteLine($"  Signature: First bytes {sector[0]:X2} {sector[1]:X2} — not a recognized boot sector");

            // Check for NTFS signature at offset 3
            if (read >= 11 && Encoding.ASCII.GetString(sector, 3, 8).Trim('\0') == "NTFS")
                Console.Error.WriteLine("  Detected: NTFS signature at offset 3");

            // Check for FAT OEM name at offset 3
            if (read >= 11)
            {
                string oem = Encoding.ASCII.GetString(sector, 3, 8).Trim('\0', ' ');
                if (!string.IsNullOrEmpty(oem))
                    Console.Error.WriteLine($"  OEM name at offset 3: \"{oem}\"");
            }

            // Check for 0xAA55 boot signature at offset 510
            if (read >= 512 && sector[510] == 0x55 && sector[511] == 0xAA)
                Console.Error.WriteLine("  Boot signature 0xAA55 found at offset 510 ✓");
        }
    }

    /// <summary>
    /// Saves the raw decrypted data to a file for external analysis.
    /// </summary>
    public void DumpRawDecryptedData(string outputPath, long maxBytes = 1024 * 1024)
    {
        _decryptedStream.Seek(0, SeekOrigin.Begin);
        long toDump = Math.Min(maxBytes, _decryptedStream.Length);

        using var output = File.Create(outputPath);
        byte[] buffer = new byte[64 * 1024];
        long written = 0;
        while (written < toDump)
        {
            int toRead = (int)Math.Min(buffer.Length, toDump - written);
            int read = _decryptedStream.Read(buffer, 0, toRead);
            if (read == 0) break;
            output.Write(buffer, 0, read);
            written += read;
        }

        Console.WriteLine($"Dumped {written} bytes of raw decrypted data to {outputPath}");
    }

    /// <summary>
    /// Returns the raw decrypted stream (for manual filesystem parsing).
    /// Position 0 = first byte of the filesystem inside the volume.
    /// </summary>
    public Stream GetDecryptedStream()
    {
        _decryptedStream.Seek(0, SeekOrigin.Begin);
        return _decryptedStream;
    }

    /// <summary>
    /// Extracts all files from the volume to the specified output directory.
    /// Returns the number of files extracted.
    /// </summary>
    /// <param name="outputDirectory">Target directory for extracted files.</param>
    /// <param name="onFileExtracted">Optional callback: (filePath, currentIndex, totalFiles).</param>
    public int ExtractAll(string outputDirectory, Action<string, int, int>? onFileExtracted = null)
    {
        Directory.CreateDirectory(outputDirectory);

        using var fs = OpenFileSystem();

        // Collect all files upfront for progress tracking
        var allFiles = CollectAllFiles(fs, "\\");
        int totalFiles = allFiles.Count;
        int extractedCount = 0;

        foreach (var file in allFiles)
        {
            string relativePath = file.TrimStart('\\', '/');
            string destFile = Path.Combine(outputDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));

            string? dir = Path.GetDirectoryName(destFile);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            try
            {
                using var srcStream = fs.OpenFile(file, FileMode.Open, FileAccess.Read);
                using var dstStream = File.Create(destFile);
                srcStream.CopyTo(dstStream);
                extractedCount++;

                if (onFileExtracted != null)
                    onFileExtracted(file, extractedCount, totalFiles);
                else
                {
                    long size = fs.GetFileLength(file);
                    Console.WriteLine($"  Extracted: {file} ({FormatSize(size)})");
                }
            }
            catch (Exception ex)
            {
                extractedCount++;
                if (onFileExtracted == null)
                    Console.WriteLine($"  WARNING: Failed to extract {file}: {ex.Message}");
            }
        }

        if (onFileExtracted == null)
            Console.WriteLine($"Extraction complete: {extractedCount} file(s) copied to {outputDirectory}");

        return extractedCount;
    }

    private static List<string> CollectAllFiles(DiscFileSystem fs, string path)
    {
        var files = new List<string>();
        try { files.AddRange(fs.GetFiles(path)); } catch { }
        try
        {
            foreach (var dir in fs.GetDirectories(path))
            {
                string dirName = Path.GetFileName(dir.TrimEnd('\\', '/'));
                if (!string.IsNullOrEmpty(dirName))
                    files.AddRange(CollectAllFiles(fs, dir));
            }
        }
        catch { }
        return files;
    }

    /// <summary>
    /// Extracts a single file from the volume.
    /// </summary>
    public void ExtractFile(string volumeFilePath, string outputPath)
    {
        using var fs = OpenFileSystem();

        string? dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using var srcStream = fs.OpenFile(volumeFilePath, FileMode.Open, FileAccess.Read);
        using var dstStream = File.Create(outputPath);
        srcStream.CopyTo(dstStream);
    }

    /// <summary>
    /// Lists all files in the volume.
    /// </summary>
    public string[] ListFiles(string path = "\\")
    {
        using var fs = OpenFileSystem();
        return fs.GetFiles(path, "*.*", SearchOption.AllDirectories);
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):F1} GB",
        >= 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        >= 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes} B"
    };

    private static void ReadFull(Stream stream, byte[] buffer, int offset, int count)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            int n = stream.Read(buffer, offset + totalRead, count - totalRead);
            if (n == 0) throw new EndOfStreamException("Unexpected end of volume file.");
            totalRead += n;
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _decryptedStream.Dispose();
            _fileStream.Dispose();
            _disposed = true;
        }
    }
}

/// <summary>
/// Thrown when the password is incorrect or the volume format is unrecognized.
/// </summary>
public class InvalidPasswordException : Exception
{
    public InvalidPasswordException(string message) : base(message) { }
}
