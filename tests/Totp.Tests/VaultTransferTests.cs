using System.Security.Cryptography;
using Totp.Core;

namespace Totp.Tests;

public class VaultTransferTests
{
    private const string Login = "alex@example.com";
    private const string Password = "test password for transfer";
    private static readonly TotpAccount Account = new("private service", "private label", "dGVzdCBzZWNyZXQ=");

    [Fact]
    public async Task FileAndQrRoundTripWithoutExposingAccounts()
    {
        var transfer = await ExportAsync([Account]);
        var file = transfer.ToFile();
        var qr = Assert.IsType<string>(transfer.ToQrText());
        Assert.DoesNotContain(Account.Name, file);
        Assert.DoesNotContain(Account.Issuer, file);
        Assert.DoesNotContain(Account.Secret, file);
        Assert.DoesNotContain(Password, file);
        Assert.Equal((1, "Test desktop", Login), (transfer.Version, transfer.Source, transfer.Login));
        Assert.Matches("^[A-F0-9]{8}$", transfer.Crc32!);
        Assert.Equal(transfer, VaultTransfer.Parse(qr));

        foreach (var text in new[] { file, qr })
        {
            var destination = new MemoryStorage();
            using var imported = await Vault.ImportTransferAsync(destination, text, Login, Password);
            Assert.Equal(Account, Assert.Single(imported.Accounts));
            using var reopened = await Vault.OpenAsync(destination, Login, Password);
            Assert.Equal(Account, Assert.Single(reopened.Accounts));
        }
    }

    [Fact]
    public async Task RejectsWrongCredentialsAndTamperingWithoutWriting()
    {
        var transfer = await ExportAsync([Account]);
        var file = transfer.ToFile();
        var destination = new MemoryStorage();
        await Assert.ThrowsAsync<CryptographicException>(() =>
            Vault.ImportTransferAsync(destination, file, Login, "incorrect password"));
        await Assert.ThrowsAsync<CryptographicException>(() =>
            Vault.ImportTransferAsync(destination, file, "wrong@example.com", Password));
        Assert.Throws<FormatException>(() => VaultTransfer.Parse(file.Replace("Test desktop", "Other device")));
        Assert.Throws<FormatException>(() => VaultTransfer.Parse((transfer with { Version = 2 }).ToFile()));
        Assert.Equal(0, destination.Writes);
    }

    [Fact]
    public async Task MergesRepeatedImportsWithoutRemovingLocalAccounts()
    {
        var transfer = await ExportAsync([Account]);
        var destination = new MemoryStorage();
        using var local = await Vault.OpenAsync(destination, Login, Password, register: true);
        var extra = Account with { Name = "local label" };
        await local.AddAsync([extra]);
        using var merged = await Vault.ImportTransferAsync(destination, transfer.ToFile(), Login, Password);
        Assert.Collection(merged.Accounts, first => Assert.Equal(extra, first), second => Assert.Equal(Account, second));
        var writes = destination.Writes;
        using var repeated = await Vault.ImportTransferAsync(destination, transfer.ToFile(), Login, Password);
        Assert.Equal(writes, destination.Writes);
        Assert.Equal(2, repeated.Accounts.Count);
    }

    [Fact]
    public async Task LargeVaultUsesFileOnlyAndLocalPasswordMustMatch()
    {
        var transfer = await ExportAsync(Enumerable.Range(0, 40).Select(i => Account with { Name = $"Service {i}" }));
        Assert.Null(transfer.ToQrText());
        var destination = new MemoryStorage();
        using var local = await Vault.OpenAsync(destination, Login, "different local password", register: true);
        await Assert.ThrowsAsync<CryptographicException>(() =>
            Vault.ImportTransferAsync(destination, transfer.ToFile(), Login, Password));
        Assert.Equal(1, destination.Writes);
        using var imported = await Vault.ImportTransferAsync(new MemoryStorage(), transfer.ToFile(), Login, Password);
        Assert.Equal(40, imported.Accounts.Count);
    }

    private static async Task<VaultTransfer> ExportAsync(IEnumerable<TotpAccount> accounts)
    {
        using var source = await Vault.OpenAsync(new MemoryStorage(), Login, Password, register: true);
        await source.AddAsync(accounts);
        return source.ExportTransfer("Test desktop");
    }

    private sealed class MemoryStorage : IVaultStorage
    {
        private readonly Dictionary<string, string> profiles = [];
        public string Location => "Test";
        public int Writes { get; private set; }
        public string? Read(string id) => profiles.GetValueOrDefault(id);

        public ValueTask ReplaceAsync(string id, string? expected, string value)
        {
            if (Read(id) != expected)
                throw new IOException("Concurrent write");
            profiles[id] = value;
            Writes++;
            return ValueTask.CompletedTask;
        }
    }
}
