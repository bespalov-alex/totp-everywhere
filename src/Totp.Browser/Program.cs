using System.Security.Cryptography;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Browser;
using Totp.App;
using Totp.Core;

[assembly: SupportedOSPlatform("browser")]

internal static partial class Program
{
    private static Task Main()
    {
        App.Storage = new BrowserStorage();
        App.KeyDeriver = DeriveKeyAsync;
        App.CapturePhoto = async () =>
        {
            var result = await Camera();
            if (result == "cancel")
                return null;
            if (result == "error")
                throw new InvalidOperationException("Камера недоступна. Проверьте разрешения и откройте приложение по HTTPS.");
            return new MemoryStream(Convert.FromBase64String(result), writable: false);
        };
        return AppBuilder.Configure<App>().StartBrowserAppAsync("app");
    }

    private static async Task<byte[]> DeriveKeyAsync(byte[] credential, byte[] salt, int iterations)
    {
        try
        {
            var result = await DeriveKey(credential, salt, iterations);
            return Convert.FromBase64String(result);
        }
        catch (JSException)
        {
            throw new CryptographicException("Web Crypto недоступен. Откройте приложение по HTTPS или на localhost.");
        }
    }

    [JSImport("globalThis.totpDeriveKey")]
    [return: JSMarshalAs<JSType.Promise<JSType.String>>]
    private static partial Task<string> DeriveKey(byte[] credential, byte[] salt, int iterations);

    [JSImport("globalThis.totpCamera")]
    [return: JSMarshalAs<JSType.Promise<JSType.String>>]
    private static partial Task<string> Camera();

    private sealed partial class BrowserStorage : IVaultStorage
    {
        [JSImport("globalThis.totpRead")]
        private static partial string? ReadLocal(string id);

        [JSImport("globalThis.totpReplace")]
        [return: JSMarshalAs<JSType.Promise<JSType.Boolean>>]
        private static partial Task<bool> ReplaceLocal(string id, string? expected, string value);

        public string? Read(string id)
        {
            try { return ReadLocal(id); }
            catch (JSException) { throw new IOException("Браузер заблокировал доступ к хранилищу."); }
        }

        public async ValueTask ReplaceAsync(string id, string? expected, string value)
        {
            try
            {
                if (!await ReplaceLocal(id, expected, value))
                    throw new IOException("Хранилище изменилось. Войдите снова и повторите операцию.");
            }
            catch (JSException)
            {
                throw new IOException("Не удалось сохранить данные в браузере.");
            }
        }
    }
}
