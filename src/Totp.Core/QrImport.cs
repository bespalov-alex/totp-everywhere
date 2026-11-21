using System.Globalization;
using Google.Protobuf;

namespace Totp.Core;

public sealed record ImportPage(IReadOnlyList<TotpAccount> Accounts, int Skipped, int BatchId = 0,
    int BatchIndex = 0, int BatchSize = 1);

public static class QrImport
{
    public static ImportPage Parse(string text)
    {
        text = text.Trim();
        if (text.Length > 64_000 || !Uri.TryCreate(text, UriKind.Absolute, out var uri))
            throw new FormatException("Invalid QR code.");
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = parameter.Split('=', 2);
            if (pair.Length != 2 || !query.TryAdd(Uri.UnescapeDataString(pair[0]), Uri.UnescapeDataString(pair[1])))
                throw new FormatException("Invalid QR code parameters.");
        }

        return (uri.Scheme, uri.Host) switch
        {
            ("otpauth", "totp") => ParseAccount(uri, query),
            ("otpauth-migration", "offline") => ParseMigration(Required(query, "data")),
            _ => throw new FormatException("This QR code does not contain supported TOTP data.")
        };
    }

    private static ImportPage ParseAccount(Uri uri, Dictionary<string, string> query)
    {
        var label = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')).Split(':', 2);
        var name = label[^1].Trim();
        var issuer = query.GetValueOrDefault("issuer", label.Length == 2 ? label[0].Trim() : "");
        var algorithm = query.GetValueOrDefault("algorithm", "SHA1").ToUpperInvariant() switch
        {
            "SHA1" => TotpAlgorithm.Sha1,
            "SHA256" => TotpAlgorithm.Sha256,
            "SHA512" => TotpAlgorithm.Sha512,
            _ => throw new FormatException("Unsupported TOTP algorithm.")
        };
        var digits = int.Parse(query.GetValueOrDefault("digits", "6"), NumberStyles.None, CultureInfo.InvariantCulture);
        var period = int.Parse(query.GetValueOrDefault("period", "30"), NumberStyles.None, CultureInfo.InvariantCulture);
        if (name.Length == 0 || digits is not (6 or 8) || period is < 1 or > 3600)
            throw new FormatException("Invalid TOTP settings.");

        return new ImportPage([new(issuer, name, NormalizeSecret(Required(query, "secret")),
            algorithm, digits, period)], 0);
    }

    private static string Required(Dictionary<string, string> query, string key) =>
        query.TryGetValue(key, out var value) ? value : throw new FormatException("The QR code is missing required data.");

    public static string NormalizeSecret(string value)
    {
        var data = new List<byte>();
        int bits = 0, buffer = 0;
        foreach (var character in string.Concat(value.Where(c => !char.IsWhiteSpace(c))).TrimEnd('=').ToUpperInvariant())
        {
            var digit = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567".IndexOf(character);
            if (digit < 0)
                throw new FormatException("Invalid Base32 secret.");
            buffer = (buffer << 5) | digit;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                data.Add((byte)(buffer >> bits));
                buffer &= (1 << bits) - 1;
            }
        }

        if (data.Count == 0 || bits >= 5 || buffer != 0)
            throw new FormatException("Invalid Base32 secret.");
        return Convert.ToBase64String(data.ToArray());
    }

    private static ImportPage ParseMigration(string data)
    {
        // Export links use either regular Base64 or unpadded Base64URL.
        var encoded = data.Replace('-', '+').Replace('_', '/');
        encoded = encoded.PadRight((encoded.Length + 3) / 4 * 4, '=');
        using var input = new CodedInputStream(Convert.FromBase64String(encoded));
        var accounts = new List<TotpAccount>();
        int version = 0, size = 1, index = 0, id = 0, skipped = 0;
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            switch (tag)
            {
                case 10:
                    var account = ReadMigratedAccount(input.ReadBytes().ToByteArray());
                    if (account is null)
                        skipped++;
                    else
                        accounts.Add(account);
                    break;
                case 16: version = input.ReadInt32(); break;
                case 24: size = input.ReadInt32(); break;
                case 32: index = input.ReadInt32(); break;
                case 40: id = input.ReadInt32(); break;
                default: input.SkipLastField(); break;
            }
        }

        // Some single-page exports omit the version or encode the batch size as zero.
        if (size == 0 && index == 0)
            size = 1;
        if (version is < 0 or > 2)
            throw new FormatException($"Google Authenticator export version {version} is unsupported.");
        if (size is < 1 or > 1000 || index < 0 || index >= size)
            throw new FormatException("The Google Authenticator export has invalid batch metadata.");
        if (accounts.Count + skipped == 0)
            throw new FormatException("The Google Authenticator export contains no accounts.");
        return new ImportPage(accounts, skipped, id, index, size);
    }

    private static TotpAccount? ReadMigratedAccount(byte[] data)
    {
        using var input = new CodedInputStream(data);
        string name = "", issuer = "", secret = "";
        int algorithm = 0, digits = 0, type = 0;
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            switch (tag)
            {
                case 10: secret = input.ReadBytes().ToBase64(); break;
                case 18: name = input.ReadString(); break;
                case 26: issuer = input.ReadString(); break;
                case 32: algorithm = input.ReadEnum(); break;
                case 40: digits = input.ReadEnum(); break;
                case 48: type = input.ReadEnum(); break;
                default: input.SkipLastField(); break;
            }
        }

        if (type != 2 || algorithm is < 0 or > 3 || digits is < 0 or > 2 ||
            secret.Length == 0 || string.IsNullOrWhiteSpace(name))
            return null;

        var hash = algorithm switch { 2 => TotpAlgorithm.Sha256, 3 => TotpAlgorithm.Sha512, _ => TotpAlgorithm.Sha1 };
        return new TotpAccount(issuer, name, secret, hash, digits == 2 ? 8 : 6);
    }
}
