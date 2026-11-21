using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace Totp.Core;

public interface IVaultStorage
{
    string Location { get; }
    string? Read(string id);
    ValueTask ReplaceAsync(string id, string? expected, string value);
}

public sealed class FileVaultStorage(string directory) : IVaultStorage
{
    public string Location => directory;

    public string? Read(string id)
    {
        var path = Path.Combine(directory, id + ".json");
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    public ValueTask ReplaceAsync(string id, string? expected, string value)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, id + ".json");
        // Serialize compare-and-replace across processes and keep the previous ciphertext until the rename.
        using var guard = File.Open(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (Read(id) != expected)
            throw new IOException("The vault changed. Sign in again and retry.");

        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None))
            {
                stream.Write(Encoding.UTF8.GetBytes(value));
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: expected is not null);
        }
        finally
        {
            File.Delete(temporary);
        }
        return ValueTask.CompletedTask;
    }
}

public delegate Task<byte[]> VaultKeyDeriver(byte[] credential, byte[] salt, int iterations);

public sealed class Vault : IDisposable
{
    private const int Iterations = 600_000;
    private readonly IVaultStorage storage;
    private readonly string login;
    private readonly string id;
    private readonly VaultKeyDeriver keyDeriver;
    private byte[] key;
    private byte[] salt;
    private string snapshot;
    private TotpAccount[] accounts;
    private bool disposed;

    private Vault(IVaultStorage storage, string login, string id, byte[] key, byte[] salt, string snapshot,
        TotpAccount[] accounts, VaultKeyDeriver keyDeriver)
    {
        this.storage = storage;
        this.login = login;
        this.id = id;
        this.key = key;
        this.salt = salt;
        this.snapshot = snapshot;
        this.accounts = accounts;
        this.keyDeriver = keyDeriver;
    }

    public IReadOnlyList<TotpAccount> Accounts => Array.AsReadOnly(accounts);

    public static async Task<Vault> OpenAsync(IVaultStorage storage, string login, string password, bool register = false,
        VaultKeyDeriver? keyDeriver = null)
    {
        var id = ProfileId(ref login, password);
        var snapshot = storage.Read(id);
        if (register && snapshot is not null)
            throw new InvalidOperationException("This username is already registered.");
        if (!register && snapshot is null)
            throw new CryptographicException("Incorrect username or password.");
        if (register)
            ValidateNewPassword(password);

        return await UnlockAsync(storage, login, id, password, snapshot, keyDeriver);
    }

    public VaultTransfer ExportTransfer(string source)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return VaultTransfer.Create(login, source, snapshot);
    }

    public static async Task<Vault> ImportTransferAsync(IVaultStorage storage, string text, string login,
        string password, VaultKeyDeriver? keyDeriver = null)
    {
        var transfer = VaultTransfer.Parse(text);
        var id = ProfileId(ref login, password);
        if (!string.Equals(login, transfer.Login, StringComparison.Ordinal))
            throw new CryptographicException("Incorrect username or password for this transfer.");

        // Authenticate the incoming vault before creating or modifying any local profile.
        var imported = await UnlockAsync(storage, login, id, password, transfer.Vault, keyDeriver);
        try
        {
            var localSnapshot = storage.Read(id);
            if (localSnapshot is null)
            {
                await storage.ReplaceAsync(id, null, transfer.Vault);
                return imported;
            }

            var local = await UnlockAsync(storage, login, id, password, localSnapshot, keyDeriver);
            try
            {
                await local.AddAsync(imported.Accounts);
                return local;
            }
            catch
            {
                local.Dispose();
                throw;
            }
            finally
            {
                imported.Dispose();
            }
        }
        catch
        {
            imported.Dispose();
            throw;
        }
    }

    private static string ProfileId(ref string login, string password)
    {
        login = login.Trim().Normalize();
        if (login.Length is < 1 or > 128 || password.Length is < 1 or > 1024)
            throw new ArgumentException("Enter your username and password.");
        var hash = new Sha256Digest();
        var loginBytes = Encoding.UTF8.GetBytes(login);
        hash.BlockUpdate(loginBytes, 0, loginBytes.Length);
        var idBytes = new byte[hash.GetDigestSize()];
        hash.DoFinal(idBytes, 0);
        return Convert.ToHexString(idBytes);
    }

    private static async Task<Vault> UnlockAsync(IVaultStorage storage, string login, string id, string password,
        string? snapshot, VaultKeyDeriver? keyDeriver)
    {
        var envelope = snapshot is null ? null : ReadEnvelope(snapshot);
        var salt = envelope?.Salt ?? RandomNumberGenerator.GetBytes(16);
        keyDeriver ??= DeriveOnDeviceAsync;
        var key = await DeriveAsync(keyDeriver, login, password, salt);
        try
        {
            var accounts = envelope is null ? [] : Decrypt(envelope, key, id);
            if (snapshot is null)
            {
                snapshot = Encrypt(accounts, key, salt, id);
                await storage.ReplaceAsync(id, null, snapshot);
            }
            return new Vault(storage, login, id, key, salt, snapshot, accounts, keyDeriver);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(key);
            throw;
        }
    }

    public async Task<int> AddAsync(IEnumerable<TotpAccount> imported)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var combined = accounts.Concat(imported).Distinct().ToArray();
        var added = combined.Length - accounts.Length;
        if (added > 0)
            await SaveAsync(combined, key, salt);
        return added;
    }

    public Task UpdateAccountAsync(TotpAccount current, TotpAccount? replacement)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var index = Array.IndexOf(accounts, current);
        if (index < 0)
            throw new InvalidOperationException("The account is no longer in the vault.");
        if (replacement is null)
            return SaveAsync(accounts.Where((_, position) => position != index).ToArray(), key, salt);
        if (replacement == current)
            return Task.CompletedTask;
        if (accounts.Contains(replacement))
            throw new InvalidOperationException("This account is already in the vault.");

        var updated = (TotpAccount[])accounts.Clone();
        updated[index] = replacement;
        return SaveAsync(updated, key, salt);
    }

    public async Task ChangePasswordAsync(string oldPassword, string newPassword)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ValidateNewPassword(newPassword);
        if (oldPassword.Length is < 1 or > 1024)
            throw new CryptographicException("Incorrect current password.");
        var oldKey = await DeriveAsync(keyDeriver, login, oldPassword, salt);
        var matches = CryptographicOperations.FixedTimeEquals(key, oldKey);
        CryptographicOperations.ZeroMemory(oldKey);
        if (!matches)
            throw new CryptographicException("Incorrect current password.");

        var newSalt = RandomNumberGenerator.GetBytes(16);
        var newKey = await DeriveAsync(keyDeriver, login, newPassword, newSalt);
        try
        {
            await SaveAsync(accounts, newKey, newSalt);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(newKey);
            throw;
        }
        CryptographicOperations.ZeroMemory(key);
        key = newKey;
        salt = newSalt;
    }

    private async Task SaveAsync(TotpAccount[] updated, byte[] encryptionKey, byte[] encryptionSalt)
    {
        var next = Encrypt(updated, encryptionKey, encryptionSalt, id);
        await storage.ReplaceAsync(id, snapshot, next);
        snapshot = next;
        accounts = updated;
    }

    private static void ValidateNewPassword(string password)
    {
        if (password.Length is < 12 or > 1024)
            throw new ArgumentException("The password must contain 12 to 1024 characters.");
    }

    private static Task<byte[]> DeriveOnDeviceAsync(byte[] credential, byte[] salt, int iterations) =>
        Task.Run(() => Rfc2898DeriveBytes.Pbkdf2(credential, salt, iterations, HashAlgorithmName.SHA256, 32));

    private static async Task<byte[]> DeriveAsync(VaultKeyDeriver derive, string login, string password, byte[] salt)
    {
        var credential = Encoding.UTF8.GetBytes(FormattableString.Invariant($"{login.Length}:{login}{password}"));
        try
        {
            return await derive(credential, salt, Iterations);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(credential);
        }
    }

    private static VaultEnvelope ReadEnvelope(string snapshot)
    {
        var envelope = JsonSerializer.Deserialize(snapshot, VaultJson.Default.VaultEnvelope);
        if (envelope is not { Version: 1, Salt.Length: 16, Nonce.Length: 12, Ciphertext.Length: >= 16 })
            throw new FormatException("Unsupported vault format.");
        return envelope;
    }

    private static string Encrypt(TotpAccount[] accounts, byte[] key, byte[] salt, string id)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(accounts, VaultJson.Default.TotpAccountArray);
        try
        {
            var encrypted = Transform(true, key, nonce, id, plaintext);
            return JsonSerializer.Serialize(new VaultEnvelope(1, salt, nonce, encrypted), VaultJson.Default.VaultEnvelope);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static TotpAccount[] Decrypt(VaultEnvelope envelope, byte[] key, string id)
    {
        byte[] plaintext;
        try
        {
            plaintext = Transform(false, key, envelope.Nonce, id, envelope.Ciphertext);
        }
        catch (Org.BouncyCastle.Crypto.InvalidCipherTextException)
        {
            throw new CryptographicException("Incorrect username or password, or the vault is damaged.");
        }

        try
        {
            return JsonSerializer.Deserialize(plaintext, VaultJson.Default.TotpAccountArray)
                ?? throw new FormatException("The vault is damaged.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static byte[] Transform(bool encrypt, byte[] key, byte[] nonce, string id, byte[] input)
    {
        var cipher = new GcmBlockCipher(new AesEngine());
        cipher.Init(encrypt, new AeadParameters(new KeyParameter(key), 128, nonce, Encoding.ASCII.GetBytes("1:" + id)));
        var output = new byte[cipher.GetOutputSize(input.Length)];
        try
        {
            var written = cipher.ProcessBytes(input, 0, input.Length, output, 0);
            cipher.DoFinal(output, written);
            return output;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(output);
            throw;
        }
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(key);
        accounts = [];
        disposed = true;
    }
}

internal sealed record VaultEnvelope(int Version, byte[] Salt, byte[] Nonce, byte[] Ciphertext);

[JsonSerializable(typeof(VaultEnvelope))]
[JsonSerializable(typeof(TotpAccount[]))]
internal partial class VaultJson : JsonSerializerContext;
