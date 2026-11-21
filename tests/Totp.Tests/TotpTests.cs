using System.Text;
using Google.Protobuf;
using Totp.Core;

namespace Totp.Tests;

public class TotpTests
{
    [Theory]
    [InlineData(TotpAlgorithm.Sha1, "12345678901234567890", "94287082", "07081804")]
    [InlineData(TotpAlgorithm.Sha256, "12345678901234567890123456789012", "46119246", "68084774")]
    [InlineData(TotpAlgorithm.Sha512, "1234567890123456789012345678901234567890123456789012345678901234", "90693936", "25091201")]
    public void MatchesRfc6238(TotpAlgorithm algorithm, string seed, string first, string second)
    {
        var account = new TotpAccount("RFC", "test", Convert.ToBase64String(Encoding.ASCII.GetBytes(seed)), algorithm, 8);
        Assert.Equal(first, account.CodeAt(DateTimeOffset.FromUnixTimeSeconds(59)));
        Assert.Equal(second, account.CodeAt(DateTimeOffset.FromUnixTimeSeconds(1_111_111_109)));
    }

    [Fact]
    public void ImportsStandardUri()
    {
        var page = QrImport.Parse("otpauth://totp/Example:alice%40example.com?secret=JBSWY3DPEHPK3PXP&issuer=Example");
        var account = Assert.Single(page.Accounts);
        Assert.Equal("alice@example.com", account.Name);
        Assert.Equal("Example", account.Issuer);
        Assert.Equal("282760", account.CodeAt(DateTimeOffset.UnixEpoch));
    }

    [Theory]
    [InlineData("otpauth://totp/Example?secret=JBSW&digits=7")]
    [InlineData("otpauth://totp/Example?secret=!&period=30")]
    [InlineData("otpauth://hotp/Example?secret=JBSWY3DPEHPK3PXP")]
    [InlineData("otpauth-migration://offline?data=")]
    public void RejectsUnsupportedUris(string uri) => Assert.Throws<FormatException>(() => QrImport.Parse(uri));

    [Fact]
    public void ImportsGoogleMigrationAndSkipsHotp()
    {
        static byte[] Write(Action<CodedOutputStream> action)
        {
            using var stream = new MemoryStream();
            using var writer = new CodedOutputStream(stream);
            action(writer);
            writer.Flush();
            return stream.ToArray();
        }

        static byte[] Entry(int type) => Write(writer =>
        {
            writer.WriteTag(10); writer.WriteBytes(ByteString.CopyFromUtf8("test secret"));
            writer.WriteTag(18); writer.WriteString("alice");
            writer.WriteTag(26); writer.WriteString("Example");
            writer.WriteTag(32); writer.WriteEnum(2);
            writer.WriteTag(40); writer.WriteEnum(2);
            writer.WriteTag(48); writer.WriteEnum(type);
        });

        var payload = Write(writer =>
        {
            writer.WriteTag(10); writer.WriteBytes(ByteString.CopyFrom(Entry(2)));
            writer.WriteTag(10); writer.WriteBytes(ByteString.CopyFrom(Entry(1)));
            writer.WriteTag(16); writer.WriteInt32(1);
            writer.WriteTag(24); writer.WriteInt32(3);
            writer.WriteTag(32); writer.WriteInt32(2);
            writer.WriteTag(40); writer.WriteInt32(42);
        });
        var page = QrImport.Parse("otpauth-migration://offline?data=" +
            Uri.EscapeDataString(Convert.ToBase64String(payload)));
        Assert.Equal((1, 42, 2, 3), (page.Skipped, page.BatchId, page.BatchIndex, page.BatchSize));
        var account = Assert.Single(page.Accounts);
        Assert.Equal(new TotpAccount("Example", "alice", "dGVzdCBzZWNyZXQ=", TotpAlgorithm.Sha256, 8), account);
    }
}
