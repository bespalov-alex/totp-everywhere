using System.Buffers.Binary;
using System.Globalization;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Macs;
using Org.BouncyCastle.Crypto.Parameters;

namespace Totp.Core;

public enum TotpAlgorithm { Sha1, Sha256, Sha512 }

public sealed record TotpAccount(string Issuer, string Name, string Secret, TotpAlgorithm Algorithm = TotpAlgorithm.Sha1,
    int Digits = 6, int Period = 30)
{
    public string CodeAt(DateTimeOffset now)
    {
        // RFC 6238 uses the number of elapsed periods as the HOTP counter.
        var counter = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, now.ToUnixTimeSeconds() / Period);
        IDigest digest = Algorithm switch
        {
            TotpAlgorithm.Sha1 => new Sha1Digest(),
            TotpAlgorithm.Sha256 => new Sha256Digest(),
            TotpAlgorithm.Sha512 => new Sha512Digest(),
            _ => throw new InvalidOperationException("Unknown TOTP algorithm.")
        };
        var mac = new HMac(digest);
        mac.Init(new KeyParameter(Convert.FromBase64String(Secret)));
        mac.BlockUpdate(counter, 0, counter.Length);
        var hash = new byte[mac.GetMacSize()];
        mac.DoFinal(hash, 0);
        var offset = hash[^1] & 15;
        var number = BinaryPrimitives.ReadInt32BigEndian(hash.AsSpan(offset)) & int.MaxValue;
        return (number % (Digits == 8 ? 100_000_000 : 1_000_000)).ToString($"D{Digits}", CultureInfo.InvariantCulture);
    }
}
