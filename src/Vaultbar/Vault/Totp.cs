using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Web;

namespace Vaultbar.Vault;

public static class Totp
{
    private const string SteamChars = "23456789BCDFGHJKMNPQRTVWXY";

    public readonly record struct Code(string Value, int SecondsRemaining, int Period);

    public static Code Generate(string spec, DateTimeOffset now)
    {
        spec = spec.Trim();
        string secret;
        var algorithm = "SHA1";
        int digits = 6, period = 30;
        var steam = false;

        if (spec.StartsWith("otpauth://", StringComparison.OrdinalIgnoreCase))
        {
            var query = HttpUtility.ParseQueryString(new Uri(spec).Query);
            secret = query["secret"] ?? "";
            algorithm = (query["algorithm"] ?? "SHA1").ToUpperInvariant();
            if (int.TryParse(query["digits"], out var d) && d is > 0 and <= 10) digits = d;
            if (int.TryParse(query["period"], out var p) && p > 0) period = p;
            steam = string.Equals(query["encoder"], "steam", StringComparison.OrdinalIgnoreCase);
        }
        else if (spec.StartsWith("steam://", StringComparison.OrdinalIgnoreCase))
        {
            secret = spec["steam://".Length..];
            steam = true;
        }
        else
        {
            secret = spec;
        }

        var key = Base32Decode(secret);
        var unix = now.ToUnixTimeSeconds();
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, unix / period);

        var hash = algorithm switch
        {
            "SHA256" => HMACSHA256.HashData(key, counter),
            "SHA512" => HMACSHA512.HashData(key, counter),
            _ => HMACSHA1.HashData(key, counter),
        };
        CryptographicOperations.ZeroMemory(key);

        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];

        string value;
        if (steam)
        {
            var sb = new StringBuilder(5);
            for (var i = 0; i < 5; i++)
            {
                sb.Append(SteamChars[binary % SteamChars.Length]);
                binary /= SteamChars.Length;
            }
            value = sb.ToString();
        }
        else
        {
            value = (binary % (long)Math.Pow(10, digits)).ToString().PadLeft(digits, '0');
        }

        return new Code(value, period - (int)(unix % period), period);
    }

    public static string Format(string code) => code.Length switch
    {
        6 => $"{code[..3]} {code[3..]}",
        8 => $"{code[..4]} {code[4..]}",
        _ => code,
    };

    public static byte[] Base32Decode(string input)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var clean = input.Replace(" ", "").Replace("-", "").TrimEnd('=').ToUpperInvariant();
        var output = new byte[clean.Length * 5 / 8];
        int buffer = 0, bits = 0, index = 0;
        foreach (var c in clean)
        {
            var v = alphabet.IndexOf(c);
            if (v < 0) throw new FormatException("Invalid TOTP secret");
            buffer = (buffer << 5) | v;
            bits += 5;
            if (bits >= 8)
            {
                output[index++] = (byte)(buffer >> (bits - 8));
                bits -= 8;
            }
        }
        return output;
    }
}
