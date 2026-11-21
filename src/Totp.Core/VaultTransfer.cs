using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Totp.Core;

public sealed record VaultTransfer(int Version, string Source, string Description, string Login, string? Crc32, string Vault)
{
    public const string QrPrefix = "totp-everywhere:transfer:";
    public const int MaximumFileBytes = 16 * 1024 * 1024;
    private const int MaximumQrBytes = 1200;

    internal static VaultTransfer Create(string login, string source, string snapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (source.Length > 64 || source.Any(char.IsControl))
            throw new ArgumentException("Invalid transfer source.", nameof(source));
        var transfer = new VaultTransfer(1, source, "Encrypted TOTP Everywhere vault", login, null, snapshot);
        return transfer with { Crc32 = transfer.CalculateChecksum() };
    }

    public string ToFile()
    {
        var json = JsonSerializer.Serialize(this, TransferJson.Default.VaultTransfer);
        if (Encoding.UTF8.GetByteCount(json) > MaximumFileBytes)
            throw new FormatException("The transfer exceeds the 16 MB limit.");
        return json;
    }

    public string? ToQrText()
    {
        // Allow margin for byte-mode QR overhead and camera readability.
        if (Vault.Length > MaximumQrBytes)
            return null;
        var text = QrPrefix + ToFile();
        return Encoding.UTF8.GetByteCount(text) <= MaximumQrBytes ? text : null;
    }

    public static VaultTransfer Parse(string text)
    {
        if (text.StartsWith(QrPrefix, StringComparison.Ordinal))
            text = text[QrPrefix.Length..];
        if (text.Length > MaximumFileBytes || Encoding.UTF8.GetByteCount(text) > MaximumFileBytes)
            throw new FormatException("The transfer exceeds the 16 MB limit.");
        VaultTransfer? transfer;
        try
        {
            transfer = JsonSerializer.Deserialize(text, TransferJson.Default.VaultTransfer);
        }
        catch (JsonException ex)
        {
            throw new FormatException("The vault transfer is not valid JSON.", ex);
        }

        if (transfer is not { Version: 1, Source.Length: >= 1 and <= 64, Description.Length: >= 1 and <= 128,
                Login.Length: >= 1 and <= 128, Crc32.Length: 8, Vault.Length: > 0 } ||
            transfer.Source.Any(char.IsControl) || transfer.Description.Any(char.IsControl))
            throw new FormatException("Invalid or unsupported vault transfer metadata.");
        if (!string.Equals(transfer.Crc32, transfer.CalculateChecksum(), StringComparison.OrdinalIgnoreCase))
            throw new FormatException("The vault transfer checksum does not match.");
        return transfer;
    }

    private string CalculateChecksum()
    {
        // CRC-32 detects corruption of the canonical metadata and ciphertext. AES-GCM authenticates the vault.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(this with { Crc32 = null }, TransferJson.Default.VaultTransfer);
        uint crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xEDB88320u);
        }
        return (~crc).ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(VaultTransfer))]
internal partial class TransferJson : JsonSerializerContext;
