using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Nexus.Service.Peripherals.Tryx.Panorama.Crypto;

/// <summary>
/// SM2 public-key encryption (GM/T 0003.4-2012) over the sm2p256v1 recommended
/// curve, pure C# via <see cref="BigInteger"/> affine-coordinate point math (no
/// BouncyCastle). Cipher layout is C1C3C2 (GM/T mode 1): C1 is the 64-byte
/// uncompressed point x||y with no format-byte prefix, C3 is a 32-byte SM3
/// digest, C2 is the KDF-XOR keystream over the plaintext. This matches
/// Kanali's own <c>nb.sm2.doEncrypt(msg, pub, 1)</c> / <c>doDecrypt(cipher,
/// priv, 1)</c> calls (decoded from the desktop app bundle) - Kanali prepends
/// a literal "04" byte to its own encrypt output; that prefixing is the
/// caller's job (see TryxCloudCatalog), not this class's wire format.
/// </summary>
public static class Sm2
{
    private static readonly BigInteger P = ParseUnsigned("FFFFFFFEFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF00000000FFFFFFFFFFFFFFFF");
    private static readonly BigInteger A = ParseUnsigned("FFFFFFFEFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF00000000FFFFFFFFFFFFFFFC");
    private static readonly BigInteger B = ParseUnsigned("28E9FA9E9D9F5E344D5A9E4BCF6509A7F39789F515AB8F92DDBCBD414D940E93");
    private static readonly BigInteger N = ParseUnsigned("FFFFFFFEFFFFFFFFFFFFFFFFFFFFFFFF7203DF6B21C6052B53BBF40939D54123");
    private static readonly BigInteger Gx = ParseUnsigned("32C4AE2C1F1981195F9904466A39C9948FE30BBFF2660BE1715A4589334C74C7");
    private static readonly BigInteger Gy = ParseUnsigned("BC3736A2F4F6779C59BDCEE36B692153D0A9877CC62A474002DF32E52139F0A0");
    private static readonly EcPoint G = new(Gx, Gy);

    public static string Encrypt(string plaintextUtf8, string pubKeyHex)
    {
        var msg = Encoding.UTF8.GetBytes(plaintextUtf8);
        var pub = ParsePoint(pubKeyHex);

        while (true)
        {
            var k = RandomScalar();
            var c1 = ScalarMultiply(k, G);
            var kp = ScalarMultiply(k, pub);
            var x2 = ToFixedBytes(kp.X);
            var y2 = ToFixedBytes(kp.Y);

            // KDF is only re-rolled for a non-empty message: IsAllZero is
            // vacuously true on a zero-length keystream, which would spin
            // this loop forever for an empty plaintext.
            var t = Kdf(Concat(x2, y2), msg.Length);
            if (msg.Length > 0 && IsAllZero(t))
            {
                continue;
            }

            var c2 = new byte[msg.Length];
            for (var i = 0; i < msg.Length; i++)
            {
                c2[i] = (byte)(msg[i] ^ t[i]);
            }

            var c3 = Sm3.Hash(Concat(x2, msg, y2));
            var cipher = Concat(ToFixedBytes(c1.X), ToFixedBytes(c1.Y), c3, c2);
            return Convert.ToHexString(cipher).ToLowerInvariant();
        }
    }

    public static string Decrypt(string cipherHex, string privKeyHex)
    {
        var data = Convert.FromHexString(cipherHex);
        if (data.Length < 64 + 32)
        {
            throw new CryptographicException("SM2 ciphertext is shorter than C1||C3.");
        }

        // C1 is a raw 64-byte x||y point; Kanali may 0x04-prefix it (SEC1
        // uncompressed tag). A raw C1 whose x coordinate starts with 0x04 is
        // ambiguous with that tag, so pick the offset that yields an on-curve
        // point rather than trusting data[0].
        int offset;
        EcPoint c1;
        if (TryReadC1(data, 0, out c1))
        {
            offset = 0;
        }
        else if (data[0] == 0x04 && TryReadC1(data, 1, out c1))
        {
            offset = 1;
        }
        else
        {
            throw new CryptographicException("SM2 C1 point is not on sm2p256v1.");
        }

        var c3 = data.AsSpan(offset + 64, 32).ToArray();
        var c2 = data.AsSpan(offset + 96).ToArray();

        var d = ParseUnsigned(privKeyHex);
        var kp = ScalarMultiply(d, c1);
        var x2 = ToFixedBytes(kp.X);
        var y2 = ToFixedBytes(kp.Y);

        var t = Kdf(Concat(x2, y2), c2.Length);
        var msg = new byte[c2.Length];
        for (var i = 0; i < c2.Length; i++)
        {
            msg[i] = (byte)(c2[i] ^ t[i]);
        }

        var u = Sm3.Hash(Concat(x2, msg, y2));
        if (!u.AsSpan().SequenceEqual(c3))
        {
            throw new CryptographicException("SM2 C3 integrity check failed.");
        }

        return Encoding.UTF8.GetString(msg);
    }

    private static bool TryReadC1(byte[] data, int offset, out EcPoint c1)
    {
        c1 = EcPoint.Infinity;
        if (data.Length - offset < 64 + 32)
        {
            return false;
        }
        var x1 = new BigInteger(data.AsSpan(offset, 32), isUnsigned: true, isBigEndian: true);
        var y1 = new BigInteger(data.AsSpan(offset + 32, 32), isUnsigned: true, isBigEndian: true);
        var point = new EcPoint(x1, y1);
        if (!IsOnCurve(point))
        {
            return false;
        }
        c1 = point;
        return true;
    }

    // GM/T 0003.3 KDF: SM3(Z || counter[BE32]) chunks, counter starts at 1.
    private static byte[] Kdf(byte[] z, int keyLenBytes)
    {
        var result = new byte[keyLenBytes];
        var pos = 0;
        var counter = 1u;
        Span<byte> counterBytes = stackalloc byte[4];
        while (pos < keyLenBytes)
        {
            BinaryPrimitives.WriteUInt32BigEndian(counterBytes, counter);
            var hash = Sm3.Hash(Concat(z, counterBytes.ToArray()));
            var copyLen = Math.Min(hash.Length, keyLenBytes - pos);
            Array.Copy(hash, 0, result, pos, copyLen);
            pos += copyLen;
            counter++;
        }
        return result;
    }

    private static bool IsAllZero(byte[] data)
    {
        foreach (var b in data)
        {
            if (b != 0)
            {
                return false;
            }
        }
        return true;
    }

    private static EcPoint ParsePoint(string pubKeyHex)
    {
        var bytes = Convert.FromHexString(pubKeyHex);
        var offset = bytes.Length == 65 && bytes[0] == 0x04 ? 1 : 0;
        if (bytes.Length - offset != 64)
        {
            throw new ArgumentException("SM2 public key must be a 64-byte x||y point, optionally 04-prefixed.", nameof(pubKeyHex));
        }
        var x = new BigInteger(bytes.AsSpan(offset, 32), isUnsigned: true, isBigEndian: true);
        var y = new BigInteger(bytes.AsSpan(offset + 32, 32), isUnsigned: true, isBigEndian: true);
        return new EcPoint(x, y);
    }

    private static BigInteger ParseUnsigned(string hex) => new(Convert.FromHexString(hex), isUnsigned: true, isBigEndian: true);

    private static byte[] ToFixedBytes(BigInteger value, int length = 32)
    {
        var bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (bytes.Length == length)
        {
            return bytes;
        }
        if (bytes.Length > length)
        {
            throw new CryptographicException("SM2 field element exceeds the expected byte length.");
        }
        var result = new byte[length];
        Array.Copy(bytes, 0, result, length - bytes.Length, bytes.Length);
        return result;
    }

    private static BigInteger RandomScalar()
    {
        Span<byte> buf = stackalloc byte[32];
        while (true)
        {
            RandomNumberGenerator.Fill(buf);
            var k = new BigInteger(buf, isUnsigned: true, isBigEndian: true);
            if (k > 0 && k < N)
            {
                return k;
            }
        }
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var total = 0;
        foreach (var part in parts)
        {
            total += part.Length;
        }
        var result = new byte[total];
        var offset = 0;
        foreach (var part in parts)
        {
            Array.Copy(part, 0, result, offset, part.Length);
            offset += part.Length;
        }
        return result;
    }

    // ── sm2p256v1 point arithmetic (affine coordinates) ────────────────────────

    private readonly struct EcPoint(BigInteger x, BigInteger y, bool isInfinity = false)
    {
        public readonly BigInteger X = x;
        public readonly BigInteger Y = y;
        public readonly bool IsInfinity = isInfinity;

        public static readonly EcPoint Infinity = new(BigInteger.Zero, BigInteger.Zero, isInfinity: true);
    }

    private static bool IsOnCurve(EcPoint p)
    {
        var lhs = Mod(p.Y * p.Y);
        var rhs = Mod(p.X * p.X * p.X + A * p.X + B);
        return lhs == rhs;
    }

    private static EcPoint ScalarMultiply(BigInteger k, EcPoint point)
    {
        var result = EcPoint.Infinity;
        var addend = point;
        while (k > 0)
        {
            if ((k & 1) == 1)
            {
                result = PointAdd(result, addend);
            }
            addend = PointDouble(addend);
            k >>= 1;
        }
        return result;
    }

    private static EcPoint PointAdd(EcPoint p1, EcPoint p2)
    {
        if (p1.IsInfinity)
        {
            return p2;
        }
        if (p2.IsInfinity)
        {
            return p1;
        }
        if (p1.X == p2.X)
        {
            return Mod(p1.Y + p2.Y) == 0 ? EcPoint.Infinity : PointDouble(p1);
        }

        var lambda = Mod((p2.Y - p1.Y) * ModInverse(Mod(p2.X - p1.X)));
        var x3 = Mod(lambda * lambda - p1.X - p2.X);
        var y3 = Mod(lambda * (p1.X - x3) - p1.Y);
        return new EcPoint(x3, y3);
    }

    private static EcPoint PointDouble(EcPoint p)
    {
        if (p.IsInfinity || p.Y == 0)
        {
            return EcPoint.Infinity;
        }

        var lambda = Mod((3 * p.X * p.X + A) * ModInverse(Mod(2 * p.Y)));
        var x3 = Mod(lambda * lambda - 2 * p.X);
        var y3 = Mod(lambda * (p.X - x3) - p.Y);
        return new EcPoint(x3, y3);
    }

    private static BigInteger Mod(BigInteger x) => ((x % P) + P) % P;

    private static BigInteger ModInverse(BigInteger x) => BigInteger.ModPow(x, P - 2, P);
}
