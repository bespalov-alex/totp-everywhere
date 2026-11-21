using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace Totp.Core;

public interface IVaultStorage
{
    string? Read(string id);
    ValueTask ReplaceAsync(string id, string? expected, string value);
}

public sealed class FileVaultStorage(string directory) : IVaultStorage
{
    public string? Read(string id)
    {
        var path = Path.Combine(directory, id + ".json");
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    public ValueTask ReplaceAsync(string id, string? expected, string value)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, id + ".json");
        using var guard = File.Open(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (Read(id) != expected)
            throw new IOException("Хранилище изменилось. Войдите снова и повторите операцию.");

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

public sealed class Vault : IDisposable
{
    private const int Iterations = 600_000;
    private readonly IVaultStorage storage;
    private readonly string login;
    private readonly string id;
    private byte[] key;
    private byte[] salt;
    private string snapshot;
    private TotpAccount[] accounts;
    private bool disposed;

    private Vault(IVaultStorage storage, string login, string id, byte[] key, byte[] salt, string snapshot,
        TotpAccount[] accounts)
    {
        this.storage = storage;
        this.login = login;
        this.id = id;
        this.key = key;
        this.salt = salt;
        this.snapshot = snapshot;
        this.accounts = accounts;
    }

    public IReadOnlyList<TotpAccount> Accounts => Array.AsReadOnly(accounts);

    public static async Task<Vault> OpenAsync(IVaultStorage storage, string login, string password, bool register = false)
    {
        login = login.Trim().Normalize();
        if (login.Length is < 1 or > 128 || password.Length is < 1 or > 1024)
            throw new ArgumentException("Введите логин и пароль.");
        var hash = new Sha256Digest();
        var loginBytes = Encoding.UTF8.GetBytes(login);
        hash.BlockUpdate(loginBytes, 0, loginBytes.Length);
        var idBytes = new byte[hash.GetDigestSize()];
        hash.DoFinal(idBytes, 0);
        var id = Convert.ToHexString(idBytes);
        var snapshot = storage.Read(id);
        if (register && snapshot is not null)
            throw new InvalidOperationException("Этот логин уже зарегистрирован.");
        if (!register && snapshot is null)
            throw new CryptographicException("Неверный логин или пароль.");
        if (register)
            ValidateNewPassword(password);

        var envelope = snapshot is null ? null : ReadEnvelope(snapshot);
        var salt = envelope?.Salt ?? RandomNumberGenerator.GetBytes(16);
        var key = Derive(login, password, salt);
        try
        {
            var accounts = envelope is null ? [] : Decrypt(envelope, key, id);
            if (snapshot is null)
            {
                snapshot = Encrypt(accounts, key, salt, id);
                await storage.ReplaceAsync(id, null, snapshot);
            }
            return new Vault(storage, login, id, key, salt, snapshot, accounts);
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

    public async Task ChangePasswordAsync(string oldPassword, string newPassword)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ValidateNewPassword(newPassword);
        if (oldPassword.Length is < 1 or > 1024)
            throw new CryptographicException("Неверный старый пароль.");
        var oldKey = Derive(login, oldPassword, salt);
        var matches = CryptographicOperations.FixedTimeEquals(key, oldKey);
        CryptographicOperations.ZeroMemory(oldKey);
        if (!matches)
            throw new CryptographicException("Неверный старый пароль.");

        var newSalt = RandomNumberGenerator.GetBytes(16);
        var newKey = Derive(login, newPassword, newSalt);
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
            throw new ArgumentException("Пароль должен содержать от 12 до 1024 символов.");
    }

    private static byte[] Derive(string login, string password, byte[] salt)
    {
        var credential = Encoding.UTF8.GetBytes($"{login.Length}:{login}{password}");
        try
        {
            var generator = new Pkcs5S2ParametersGenerator(new Sha256Digest());
            generator.Init(credential, salt, Iterations);
            return ((KeyParameter)generator.GenerateDerivedMacParameters(256)).GetKey();
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
            throw new FormatException("Неподдерживаемый формат хранилища.");
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
            throw new CryptographicException("Неверный логин или пароль либо хранилище повреждено.");
        }

        try
        {
            return JsonSerializer.Deserialize(plaintext, VaultJson.Default.TotpAccountArray)
                ?? throw new FormatException("Хранилище повреждено.");
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
