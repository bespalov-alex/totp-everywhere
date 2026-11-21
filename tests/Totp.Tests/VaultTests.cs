using System.Security.Cryptography;
using Totp.Core;

namespace Totp.Tests;

public class VaultTests
{
    private const string Login = "alice@example.com";
    private const string OldPassword = "first test password";
    private const string NewPassword = "second test password";
    private static readonly TotpAccount Account = new("Example", "private label", "dGVzdCBzZWNyZXQ=");

    [Fact]
    public async Task EncryptsAndRotatesCredentials()
    {
        var storage = new MemoryStorage();
        using var vault = await Vault.OpenAsync(storage, Login, OldPassword, register: true);
        Assert.Equal(1, await vault.AddAsync([Account, Account]));
        Assert.Equal(0, await vault.AddAsync([Account]));
        var stored = Assert.Single(storage.Values.Values);
        Assert.DoesNotContain(Account.Name, stored);
        Assert.DoesNotContain(Account.Secret, stored);
        await Assert.ThrowsAsync<CryptographicException>(() => vault.ChangePasswordAsync("incorrect", NewPassword));
        Assert.Equal(stored, Assert.Single(storage.Values.Values));

        await vault.ChangePasswordAsync(OldPassword, NewPassword);
        Assert.NotEqual(stored, Assert.Single(storage.Values.Values));
        await Assert.ThrowsAsync<CryptographicException>(() => Vault.OpenAsync(storage, Login, OldPassword));
        await Assert.ThrowsAsync<CryptographicException>(() => Vault.OpenAsync(storage, "another-login", NewPassword));
        using var reopened = await Vault.OpenAsync(storage, Login, NewPassword);
        Assert.Equal(Account, Assert.Single(reopened.Accounts));
    }

    [Fact]
    public async Task RejectsStaleWrites()
    {
        var storage = new MemoryStorage();
        using var first = await Vault.OpenAsync(storage, Login, OldPassword, register: true);
        using var stale = await Vault.OpenAsync(storage, Login, OldPassword);
        await first.AddAsync([Account]);
        await Assert.ThrowsAsync<IOException>(() => stale.AddAsync([Account with { Name = "other" }]));
        Assert.Empty(stale.Accounts);
    }

    [Fact]
    public async Task RejectsTamperedCiphertext()
    {
        var storage = new MemoryStorage();
        using var vault = await Vault.OpenAsync(storage, Login, OldPassword, register: true);
        await vault.AddAsync([Account]);
        var (id, value) = Assert.Single(storage.Values);
        var start = value.IndexOf("\"Ciphertext\":\"", StringComparison.Ordinal) + "\"Ciphertext\":\"".Length;
        storage.Values[id] = value[..start] + (value[start] == 'A' ? 'B' : 'A') + value[(start + 1)..];
        await Assert.ThrowsAsync<CryptographicException>(() => Vault.OpenAsync(storage, Login, OldPassword));
    }

    [Fact]
    public async Task EditsAndRemovesAccountsInEncryptedVault()
    {
        var storage = new MemoryStorage();
        using var vault = await Vault.OpenAsync(storage, Login, OldPassword, register: true);
        var other = Account with { Name = "other" };
        var edited = Account with { Name = "renamed", Period = 60 };
        await vault.AddAsync([Account, other]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => vault.UpdateAccountAsync(Account, other));
        await vault.UpdateAccountAsync(Account, edited);
        await vault.UpdateAccountAsync(other, null);

        using var reopened = await Vault.OpenAsync(storage, Login, OldPassword);
        Assert.Equal(edited, Assert.Single(reopened.Accounts));
        Assert.DoesNotContain("renamed", Assert.Single(storage.Values.Values));
    }

    private sealed class MemoryStorage : IVaultStorage
    {
        public string Location => "Test storage";
        public Dictionary<string, string> Values { get; } = [];
        public string? Read(string id) => Values.GetValueOrDefault(id);
        public ValueTask ReplaceAsync(string id, string? expected, string value)
        {
            if (Read(id) != expected)
                throw new IOException("Write conflict.");
            Values[id] = value;
            return ValueTask.CompletedTask;
        }
    }
}
