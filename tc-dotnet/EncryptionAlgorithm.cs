using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Parameters;
using System.Security.Cryptography;

namespace TrueCryptReader;

/// <summary>
/// Wraps a single cipher (AES, Serpent, or Twofish) for block encryption/decryption.
/// All ciphers use 256-bit keys and 128-bit (16-byte) blocks.
/// </summary>
public class CipherEngine
{
    private readonly IBlockCipher _encryptEngine;
    private readonly IBlockCipher _decryptEngine;

    public string Name { get; }
    public int KeySize => 32;   // All TrueCrypt ciphers use 256-bit keys
    public int BlockSize => 16; // All modern TrueCrypt ciphers use 128-bit blocks

    public CipherEngine(string name, byte[] key)
    {
        Name = name;
        _encryptEngine = CreateEngine(name);
        _decryptEngine = CreateEngine(name);

        _encryptEngine.Init(true, new KeyParameter(key));
        _decryptEngine.Init(false, new KeyParameter(key));
    }

    private static IBlockCipher CreateEngine(string name) => name switch
    {
        "AES" => new AesEngine(),
        "Serpent" => new SerpentEngine(),
        "Twofish" => new TwofishEngine(),
        _ => throw new ArgumentException($"Unknown cipher: {name}")
    };

    public void EncryptBlock(byte[] data, int offset)
    {
        _encryptEngine.ProcessBlock(data, offset, data, offset);
    }

    public void DecryptBlock(byte[] data, int offset)
    {
        _decryptEngine.ProcessBlock(data, offset, data, offset);
    }
}

/// <summary>
/// Represents a TrueCrypt Encryption Algorithm (EA) — one or more ciphers in a cascade.
/// Matches the EncryptionAlgorithms[] table in Crypto.c.
/// </summary>
public class EncryptionAlgorithm
{
    public string Name { get; }
    public string[] CipherNames { get; }
    public int KeySize => CipherNames.Length * 32; // Each cipher uses a 32-byte key

    public EncryptionAlgorithm(string name, params string[] cipherNames)
    {
        Name = name;
        CipherNames = cipherNames;
    }

    /// <summary>
    /// All encryption algorithms from TrueCrypt's EncryptionAlgorithms[] table.
    /// </summary>
    public static readonly EncryptionAlgorithm[] All =
    [
        new("AES", "AES"),
        new("Serpent", "Serpent"),
        new("Twofish", "Twofish"),
        new("Twofish-AES", "Twofish", "AES"),
        new("Serpent-Twofish-AES", "Serpent", "Twofish", "AES"),
        new("AES-Serpent", "AES", "Serpent"),
        new("AES-Twofish-Serpent", "AES", "Twofish", "Serpent"),
        new("Serpent-Twofish", "Serpent", "Twofish"),
    ];

    /// <summary>
    /// Creates cipher engines from key material. For XTS, both primary and secondary key sets.
    /// </summary>
    public (CipherEngine[] primary, CipherEngine[] secondary) CreateEngines(byte[] derivedKey)
    {
        int keySize = KeySize;
        var primary = new CipherEngine[CipherNames.Length];
        var secondary = new CipherEngine[CipherNames.Length];

        for (int i = 0; i < CipherNames.Length; i++)
        {
            byte[] pk = new byte[32];
            byte[] sk = new byte[32];
            Buffer.BlockCopy(derivedKey, i * 32, pk, 0, 32);
            Buffer.BlockCopy(derivedKey, keySize + i * 32, sk, 0, 32);
            primary[i] = new CipherEngine(CipherNames[i], pk);
            secondary[i] = new CipherEngine(CipherNames[i], sk);
        }

        return (primary, secondary);
    }
}
