using System.Diagnostics;

namespace TrueCryptReader;

/// <summary>
/// Mounts a decrypted TrueCrypt volume as a Windows drive letter using a temporary VHD file.
/// Supports writing changes back to the encrypted volume on unmount.
/// Requires Administrator privileges for VHD mount/unmount operations.
/// </summary>
public class VolumeMounter : IDisposable
{
    private readonly string _volumePath;
    private readonly string _password;
    private string? _vhdPath;
    private string? _driveLetter;
    private bool _mounted;
    private bool _disposed;

    public string? DriveLetter => _driveLetter;
    public bool IsMounted => _mounted;

    public VolumeMounter(string volumePath, string password)
    {
        _volumePath = volumePath;
        _password = password;
    }

    /// <summary>
    /// Mounts the TrueCrypt volume as a Windows drive.
    /// </summary>
    public string Mount(Action<string>? statusCallback = null)
    {
        if (_mounted)
            throw new InvalidOperationException("Volume is already mounted.");

        void Report(string msg) { if (statusCallback != null) statusCallback(msg); else Console.WriteLine(msg); }

        Report("Decrypting volume...");
        using var volume = TrueCryptVolume.Open(_volumePath, _password);
        var decryptedStream = volume.GetDecryptedStream();

        // Create temp VHD file
        _vhdPath = Path.Combine(Path.GetTempPath(), $"tc_{Guid.NewGuid():N}.vhd");

        Report("Creating temporary VHD...");
        CreateVhdFromDecryptedData(decryptedStream, volume.Header, _vhdPath);

        Report("Mounting VHD...");
        MountVhd(_vhdPath);

        Report("Waiting for Windows to recognize the volume...");
        Thread.Sleep(2000);

        Report("Detecting drive letter...");
        _driveLetter = DetectDriveLetter(_vhdPath);
        if (_driveLetter == null)
        {
            Report("Retrying drive letter detection...");
            Thread.Sleep(3000);
            _driveLetter = DetectDriveLetter(_vhdPath);
        }

        _mounted = true;

        if (_driveLetter != null)
            Report($"Volume mounted as {_driveLetter}");
        else
            Report("VHD mounted but no drive letter was assigned.");

        return _driveLetter ?? "unknown";
    }

    /// <summary>
    /// Unmounts the VHD and writes changes back to the TrueCrypt volume.
    /// </summary>
    public void Unmount(Action<string>? statusCallback = null, Action<long, long>? writeProgress = null)
    {
        if (!_mounted || _vhdPath == null)
            return;

        void Report(string msg) { if (statusCallback != null) statusCallback(msg); else Console.WriteLine(msg); }

        Report("Unmounting VHD...");
        DismountVhd(_vhdPath);
        _mounted = false;

        Thread.Sleep(1000);

        Report("Writing changes back to encrypted volume...");
        WriteBackToVolume(writeProgress);

        // Clean up temp file
        try
        {
            if (File.Exists(_vhdPath))
                File.Delete(_vhdPath);
            Report("Temporary VHD cleaned up.");
        }
        catch (Exception ex)
        {
            Report($"Warning: Could not delete temp VHD: {ex.Message}");
        }

        _driveLetter = null;
        _vhdPath = null;
    }

    private static void CreateVhdFromDecryptedData(Stream decryptedStream, VolumeHeader header, string vhdPath)
    {
        long dataSize = decryptedStream.Length;

        // VHD needs to be disk-sized (must include space for MBR + partition alignment)
        // We use 1MB alignment for the partition start (standard for modern disks)
        const long partitionOffset = 1024 * 1024; // 1MB
        long diskSize = partitionOffset + dataSize;

        // Round up to nearest 512 bytes
        diskSize = (diskSize + 511) & ~511L;

        using var vhdStream = new FileStream(vhdPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);

        // Write MBR with one partition
        byte[] mbr = CreateMbr(partitionOffset, dataSize, diskSize);
        vhdStream.Write(mbr, 0, 512);

        // Write padding to partition offset
        byte[] padding = new byte[partitionOffset - 512];
        vhdStream.Write(padding, 0, padding.Length);

        // Copy decrypted filesystem data
        decryptedStream.Seek(0, SeekOrigin.Begin);
        byte[] buffer = new byte[64 * 1024];
        long remaining = dataSize;
        while (remaining > 0)
        {
            int toRead = (int)Math.Min(buffer.Length, remaining);
            int read = decryptedStream.Read(buffer, 0, toRead);
            if (read == 0) break;
            vhdStream.Write(buffer, 0, read);
            remaining -= read;
        }

        // Pad to full disk size if needed
        long currentPos = vhdStream.Position;
        if (currentPos < diskSize)
        {
            vhdStream.SetLength(diskSize + 512); // +512 for VHD footer
        }

        // Write VHD footer (512 bytes at end of file)
        vhdStream.Seek(diskSize, SeekOrigin.Begin);
        byte[] footer = CreateVhdFooter(diskSize);
        vhdStream.Write(footer, 0, 512);
        vhdStream.Flush();
    }

    private static byte[] CreateMbr(long partitionOffset, long partitionSize, long diskSize)
    {
        byte[] mbr = new byte[512];

        // MBR partition table starts at offset 446
        int partEntry = 446;

        // Partition 1 entry (16 bytes)
        mbr[partEntry + 0] = 0x00;  // Not active/bootable
        // CHS of first sector (use LBA, set CHS to 0xFE,0xFF,0xFF for large disks)
        mbr[partEntry + 1] = 0xFE;
        mbr[partEntry + 2] = 0xFF;
        mbr[partEntry + 3] = 0xFF;

        mbr[partEntry + 4] = 0x07;  // Partition type: NTFS/exFAT/HPFS

        // CHS of last sector
        mbr[partEntry + 5] = 0xFE;
        mbr[partEntry + 6] = 0xFF;
        mbr[partEntry + 7] = 0xFF;

        // LBA of first sector
        uint lbaStart = (uint)(partitionOffset / 512);
        mbr[partEntry + 8] = (byte)(lbaStart);
        mbr[partEntry + 9] = (byte)(lbaStart >> 8);
        mbr[partEntry + 10] = (byte)(lbaStart >> 16);
        mbr[partEntry + 11] = (byte)(lbaStart >> 24);

        // Number of sectors in partition
        uint sectorCount = (uint)(partitionSize / 512);
        mbr[partEntry + 12] = (byte)(sectorCount);
        mbr[partEntry + 13] = (byte)(sectorCount >> 8);
        mbr[partEntry + 14] = (byte)(sectorCount >> 16);
        mbr[partEntry + 15] = (byte)(sectorCount >> 24);

        // Boot signature
        mbr[510] = 0x55;
        mbr[511] = 0xAA;

        return mbr;
    }

    private static byte[] CreateVhdFooter(long diskSize)
    {
        byte[] footer = new byte[512];

        // Cookie: "conectix"
        byte[] cookie = "conectix"u8.ToArray();
        Buffer.BlockCopy(cookie, 0, footer, 0, 8);

        // Features: 0x00000002 (Reserved, must be set)
        footer[8] = 0x00; footer[9] = 0x00; footer[10] = 0x00; footer[11] = 0x02;

        // File Format Version: 0x00010000 (1.0)
        footer[12] = 0x00; footer[13] = 0x01; footer[14] = 0x00; footer[15] = 0x00;

        // Data Offset: 0xFFFFFFFFFFFFFFFF (fixed disk, no dynamic header)
        for (int i = 16; i < 24; i++) footer[i] = 0xFF;

        // Timestamp: seconds since Jan 1, 2000 12:00:00 AM UTC
        uint timestamp = (uint)(DateTime.UtcNow - new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        footer[24] = (byte)(timestamp >> 24);
        footer[25] = (byte)(timestamp >> 16);
        footer[26] = (byte)(timestamp >> 8);
        footer[27] = (byte)(timestamp);

        // Creator Application: "tcrd" (TrueCrypt Reader)
        footer[28] = (byte)'t'; footer[29] = (byte)'c'; footer[30] = (byte)'r'; footer[31] = (byte)'d';

        // Creator Version: 1.0
        footer[32] = 0x00; footer[33] = 0x01; footer[34] = 0x00; footer[35] = 0x00;

        // Creator Host OS: Windows (0x5769326B = "Wi2k")
        footer[36] = 0x57; footer[37] = 0x69; footer[38] = 0x32; footer[39] = 0x6B;

        // Original Size (big-endian 8 bytes)
        WriteBE64(footer, 40, (ulong)diskSize);

        // Current Size (big-endian 8 bytes)
        WriteBE64(footer, 48, (ulong)diskSize);

        // Disk Geometry (CHS)
        ComputeChs(diskSize, out ushort cylinders, out byte heads, out byte sectorsPerTrack);
        footer[56] = (byte)(cylinders >> 8);
        footer[57] = (byte)(cylinders);
        footer[58] = heads;
        footer[59] = sectorsPerTrack;

        // Disk Type: Fixed (0x00000002)
        footer[60] = 0x00; footer[61] = 0x00; footer[62] = 0x00; footer[63] = 0x02;

        // Unique ID (16 bytes UUID)
        byte[] uuid = Guid.NewGuid().ToByteArray();
        Buffer.BlockCopy(uuid, 0, footer, 68, 16);

        // Saved State: 0
        footer[84] = 0;

        // Reserved: 427 bytes (already zero)

        // Compute checksum (one's complement of sum of all bytes except checksum field at offset 64-67)
        uint checksum = 0;
        for (int i = 0; i < 512; i++)
        {
            if (i >= 64 && i < 68) continue; // Skip checksum field
            checksum += footer[i];
        }
        checksum = ~checksum;
        footer[64] = (byte)(checksum >> 24);
        footer[65] = (byte)(checksum >> 16);
        footer[66] = (byte)(checksum >> 8);
        footer[67] = (byte)(checksum);

        return footer;
    }

    private static void ComputeChs(long diskSize, out ushort cylinders, out byte heads, out byte sectorsPerTrack)
    {
        long totalSectors = diskSize / 512;

        if (totalSectors > 65535 * 16 * 255)
            totalSectors = 65535 * 16 * 255;

        if (totalSectors >= 65535 * 16 * 63)
        {
            sectorsPerTrack = 255;
            heads = 16;
            cylinders = (ushort)(totalSectors / (16 * 255));
        }
        else
        {
            sectorsPerTrack = 17;
            long tmp = totalSectors / 17;
            heads = (byte)((tmp + 1023) / 1024);
            if (heads < 4) heads = 4;

            if (tmp >= heads * 1024 || heads > 16)
            {
                sectorsPerTrack = 31;
                heads = 16;
                tmp = totalSectors / 31;
            }

            if (tmp >= heads * 1024)
            {
                sectorsPerTrack = 63;
                heads = 16;
                tmp = totalSectors / 63;
            }

            cylinders = (ushort)(tmp / heads);
        }

        if (cylinders > 65535) cylinders = 65535;
    }

    private static void WriteBE64(byte[] data, int offset, ulong value)
    {
        data[offset + 0] = (byte)(value >> 56);
        data[offset + 1] = (byte)(value >> 48);
        data[offset + 2] = (byte)(value >> 40);
        data[offset + 3] = (byte)(value >> 32);
        data[offset + 4] = (byte)(value >> 24);
        data[offset + 5] = (byte)(value >> 16);
        data[offset + 6] = (byte)(value >> 8);
        data[offset + 7] = (byte)(value);
    }

    private static void MountVhd(string vhdPath)
    {
        string script = $"Mount-DiskImage -ImagePath '{vhdPath}' -StorageType VHD -Access ReadWrite";
        RunPowerShell(script);
    }

    private static void DismountVhd(string vhdPath)
    {
        string script = $"Dismount-DiskImage -ImagePath '{vhdPath}'";
        RunPowerShell(script);
    }

    private static string? DetectDriveLetter(string vhdPath)
    {
        try
        {
            // Use PowerShell to get the drive letter from the mounted VHD
            string script = $@"
                $image = Get-DiskImage -ImagePath '{vhdPath}'
                if ($image.Number -ne $null) {{
                    $disk = Get-Disk -Number $image.Number
                    $partitions = Get-Partition -DiskNumber $disk.Number -ErrorAction SilentlyContinue
                    foreach ($p in $partitions) {{
                        if ($p.DriveLetter) {{
                            Write-Output ""$($p.DriveLetter):""
                            break
                        }}
                    }}
                }}";

            var result = RunPowerShellCapture(script);
            var letter = result?.Trim();
            if (!string.IsNullOrEmpty(letter) && letter.Length == 2 && letter[1] == ':')
                return letter;
        }
        catch { }

        return null;
    }

    private void WriteBackToVolume(Action<long, long>? writeProgress = null)
    {
        if (_vhdPath == null || !File.Exists(_vhdPath))
        {
            Console.WriteLine("Warning: VHD file not found, cannot write back changes.");
            return;
        }

        // Open the volume for writing
        using var volume = TrueCryptVolume.Open(_volumePath, _password, writable: true);
        var decryptedStream = volume.GetDecryptedStream();
        long dataSize = decryptedStream.Length;

        // Read partition data from VHD (skip MBR + alignment padding)
        const long partitionOffset = 1024 * 1024; // 1MB, matching CreateVhdFromDecryptedData

        using var vhdStream = new FileStream(_vhdPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        vhdStream.Seek(partitionOffset, SeekOrigin.Begin);

        // Write back to the decrypted stream (which handles encryption)
        decryptedStream.Seek(0, SeekOrigin.Begin);
        byte[] buffer = new byte[64 * 1024];
        long remaining = dataSize;
        long written = 0;

        while (remaining > 0)
        {
            int toRead = (int)Math.Min(buffer.Length, remaining);
            int read = vhdStream.Read(buffer, 0, toRead);
            if (read == 0) break;
            decryptedStream.Write(buffer, 0, read);
            remaining -= read;
            written += read;

            if (writeProgress != null)
                writeProgress(written, dataSize);
            else if (written % (1024 * 1024) == 0)
                Console.Write($"\r  Writing: {written / (1024 * 1024)} MB / {dataSize / (1024 * 1024)} MB");
        }

        if (writeProgress == null)
            Console.WriteLine($"\r  Written: {written / (1024 * 1024)} MB total                    ");

        decryptedStream.Flush();
    }

    private static void RunPowerShell(string script)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{script}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)!;
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"PowerShell command failed (exit code {process.ExitCode}): {error}\n" +
                "Make sure you are running as Administrator.");
        }
    }

    private static string? RunPowerShellCapture(string script)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{script}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)!;
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            if (_mounted)
            {
                try { Unmount(); } catch { }
            }
            _disposed = true;
        }
    }
}
