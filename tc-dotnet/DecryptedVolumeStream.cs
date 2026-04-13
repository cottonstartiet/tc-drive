namespace TrueCryptReader;

/// <summary>
/// A read-only stream that decrypts TrueCrypt volume data on-the-fly.
/// Wraps the raw volume file and provides transparent XTS decryption.
/// The stream starts at the data area (offset 0 = first byte of filesystem).
/// </summary>
public class DecryptedVolumeStream : Stream
{
    private readonly Stream _baseStream;
    private readonly long _dataAreaOffset;
    private long _dataAreaLength;
    private readonly CipherEngine[] _primaryEngines;
    private readonly CipherEngine[] _secondaryEngines;
    private long _position;

    /// <summary>
    /// Creates a decrypted volume stream.
    /// </summary>
    /// <param name="baseStream">The raw volume file stream (must be seekable).</param>
    /// <param name="header">The decrypted volume header.</param>
    public DecryptedVolumeStream(Stream baseStream, VolumeHeader header, bool writable = false)
    {
        _baseStream = baseStream ?? throw new ArgumentNullException(nameof(baseStream));
        _writable = writable;

        // Use EncryptedAreaStart from header if available, otherwise default to 128KB
        if (header.IsHiddenVolume)
        {
            _dataAreaOffset = baseStream.Length - (long)header.HiddenVolumeSize - TrueCryptConstants.VolumeHeaderGroupSize;
        }
        else if (header.EncryptedAreaStart > 0)
        {
            _dataAreaOffset = (long)header.EncryptedAreaStart;
        }
        else
        {
            _dataAreaOffset = TrueCryptConstants.VolumeDataOffset;
        }

        // Use EncryptedAreaLength if available, otherwise VolumeSize
        _dataAreaLength = header.EncryptedAreaLength > 0
            ? (long)header.EncryptedAreaLength
            : (long)header.VolumeSize;

        if (_dataAreaLength <= 0)
            _dataAreaLength = baseStream.Length - _dataAreaOffset;

        // Initialize cipher engines from master key data
        var ea = header.EncryptionAlgorithm;
        int keySize = ea.KeySize;

        _primaryEngines = new CipherEngine[ea.CipherNames.Length];
        _secondaryEngines = new CipherEngine[ea.CipherNames.Length];

        for (int i = 0; i < ea.CipherNames.Length; i++)
        {
            byte[] pk = new byte[32];
            byte[] sk = new byte[32];
            Buffer.BlockCopy(header.MasterKeyData, i * 32, pk, 0, 32);
            Buffer.BlockCopy(header.MasterKeyData, keySize + i * 32, sk, 0, 32);
            _primaryEngines[i] = new CipherEngine(ea.CipherNames[i], pk);
            _secondaryEngines[i] = new CipherEngine(ea.CipherNames[i], sk);
        }

        _position = 0;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => _writable;
    public override long Length => _dataAreaLength;
    public long DataAreaOffset => _dataAreaOffset;
    private readonly bool _writable;

    public override long Position
    {
        get => _position;
        set => _position = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_position >= _dataAreaLength)
            return 0;

        // Clamp to available data
        long available = _dataAreaLength - _position;
        if (count > available)
            count = (int)available;
        if (count <= 0)
            return 0;

        int totalRead = 0;
        int bufferOffset = offset;

        while (count > 0)
        {
            // Determine which 512-byte data unit we're in
            long sectorIndex = _position / TrueCryptConstants.EncryptionDataUnitSize;
            int offsetInSector = (int)(_position % TrueCryptConstants.EncryptionDataUnitSize);

            // Read the full encrypted sector
            byte[] sector = new byte[TrueCryptConstants.EncryptionDataUnitSize];
            long fileOffset = _dataAreaOffset + sectorIndex * TrueCryptConstants.EncryptionDataUnitSize;

            _baseStream.Seek(fileOffset, SeekOrigin.Begin);
            int bytesRead = ReadFull(_baseStream, sector, 0, sector.Length);
            if (bytesRead == 0)
                break;

            // Decrypt the sector using XTS
            // CRITICAL: TrueCrypt computes data unit number from the ABSOLUTE file offset,
            // not from the start of the data area. So first data sector at file offset
            // 131072 uses data unit number 256 (= 131072 / 512), not 0.
            ulong dataUnitNo = (ulong)(fileOffset / TrueCryptConstants.EncryptionDataUnitSize);
            XtsDecryptor.DecryptXtsCascade(sector, 0, sector.Length, dataUnitNo,
                _primaryEngines, _secondaryEngines);

            // Copy the requested portion
            int toCopy = Math.Min(count, TrueCryptConstants.EncryptionDataUnitSize - offsetInSector);
            toCopy = Math.Min(toCopy, bytesRead - offsetInSector);
            if (toCopy <= 0)
                break;

            Buffer.BlockCopy(sector, offsetInSector, buffer, bufferOffset, toCopy);
            _position += toCopy;
            bufferOffset += toCopy;
            totalRead += toCopy;
            count -= toCopy;
        }

        return totalRead;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        _position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _dataAreaLength + offset,
            _ => throw new ArgumentException("Invalid SeekOrigin")
        };
        return _position;
    }

    public override void Flush() { _baseStream.Flush(); }

    public override void SetLength(long value)
    {
        if (!_writable) throw new NotSupportedException("Stream is read-only.");
        _dataAreaLength = value;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        if (!_writable) throw new NotSupportedException("Stream is read-only.");
        if (_position + count > _dataAreaLength)
            throw new IOException("Write would exceed data area length.");

        int written = 0;
        while (count > 0)
        {
            long sectorIndex = _position / TrueCryptConstants.EncryptionDataUnitSize;
            int offsetInSector = (int)(_position % TrueCryptConstants.EncryptionDataUnitSize);

            // If writing a partial sector, read-modify-write
            byte[] sector = new byte[TrueCryptConstants.EncryptionDataUnitSize];
            long fileOffset = _dataAreaOffset + sectorIndex * TrueCryptConstants.EncryptionDataUnitSize;

            if (offsetInSector != 0 || count < TrueCryptConstants.EncryptionDataUnitSize)
            {
                // Read existing encrypted sector and decrypt it
                _baseStream.Seek(fileOffset, SeekOrigin.Begin);
                ReadFull(_baseStream, sector, 0, sector.Length);
                ulong readUnit = (ulong)(fileOffset / TrueCryptConstants.EncryptionDataUnitSize);
                XtsDecryptor.DecryptXtsCascade(sector, 0, sector.Length, readUnit,
                    _primaryEngines, _secondaryEngines);
            }

            // Copy new data into the plaintext sector
            int toCopy = Math.Min(count, TrueCryptConstants.EncryptionDataUnitSize - offsetInSector);
            Buffer.BlockCopy(buffer, offset + written, sector, offsetInSector, toCopy);

            // Encrypt the sector
            ulong dataUnitNo = (ulong)(fileOffset / TrueCryptConstants.EncryptionDataUnitSize);
            XtsEncryptor.EncryptXtsCascade(sector, 0, sector.Length, dataUnitNo,
                _primaryEngines, _secondaryEngines);

            // Write encrypted sector to base stream
            _baseStream.Seek(fileOffset, SeekOrigin.Begin);
            _baseStream.Write(sector, 0, sector.Length);

            _position += toCopy;
            written += toCopy;
            count -= toCopy;
        }
    }

    /// <summary>Reads exactly 'count' bytes, or as many as available.</summary>
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
}
