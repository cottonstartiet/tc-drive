using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using System.Security.Cryptography;

namespace TrueCryptReader;

/// <summary>
/// PBKDF2 key derivation supporting all TrueCrypt PRFs.
/// </summary>
public enum TrueCryptPrf
{
    RipeMd160 = 1,
    Sha512 = 2,
    Whirlpool = 3,
    Sha1 = 4,
}

public static class KeyDerivation
{
    /// <summary>All PRFs to try, in order.</summary>
    public static readonly TrueCryptPrf[] AllPrfs =
    [
        TrueCryptPrf.RipeMd160,
        TrueCryptPrf.Sha512,
        TrueCryptPrf.Whirlpool,
        TrueCryptPrf.Sha1,
    ];

    /// <summary>
    /// Returns the PBKDF2 iteration count for a given PRF, matching TrueCrypt's Pkcs5.c.
    /// </summary>
    public static int GetIterations(TrueCryptPrf prf, bool boot)
    {
        return prf switch
        {
            TrueCryptPrf.RipeMd160 => boot ? 1000 : 2000,
            TrueCryptPrf.Sha512 => 1000,
            TrueCryptPrf.Whirlpool => 1000,
            TrueCryptPrf.Sha1 => 2000,
            _ => throw new ArgumentException($"Unknown PRF: {prf}")
        };
    }

    /// <summary>
    /// Derives a key using PBKDF2-HMAC with the specified PRF.
    /// Output is 256 bytes (MASTER_KEYDATA_SIZE) to cover all cascade + XTS keys.
    /// </summary>
    public static byte[] DeriveKey(byte[] password, byte[] salt, TrueCryptPrf prf, bool boot)
    {
        int iterations = GetIterations(prf, boot);
        int outputLen = TrueCryptConstants.MasterKeyDataSize; // 256 bytes

        IDigest digest = prf switch
        {
            TrueCryptPrf.RipeMd160 => new RipeMD160Digest(),
            TrueCryptPrf.Sha512 => new Sha512Digest(),
            TrueCryptPrf.Whirlpool => new WhirlpoolDigest(),
            TrueCryptPrf.Sha1 => new Sha1Digest(),
            _ => throw new ArgumentException($"Unknown PRF: {prf}")
        };

        var generator = new Pkcs5S2ParametersGenerator(digest);
        generator.Init(password, salt, iterations);
        var keyParam = (KeyParameter)generator.GenerateDerivedMacParameters(outputLen * 8);
        return keyParam.GetKey();
    }
}
