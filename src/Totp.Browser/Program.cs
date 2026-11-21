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
        App.ExportDriveBackup = bytes => DriveOperation(() => DriveExport(bytes));
        App.ImportDriveBackup = () => DriveOperation(DriveImport);
        App.DownloadFile = (name, bytes) =>
        {
            try { Download(name, bytes); }
            catch (JSException ex) { throw new IOException("The browser could not download the transfer.", ex); }
        };
        App.CapturePhoto = async () =>
        {
            var result = await Camera();
            if (result == "cancel")
                return null;
            if (result == "error")
                throw new InvalidOperationException("Camera unavailable. Check permissions and open the app over HTTPS.");
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
            throw new CryptographicException("Web Crypto is unavailable. Open the app over HTTPS or localhost.");
        }
    }

    [JSImport("globalThis.totpDeriveKey")]
    [return: JSMarshalAs<JSType.Promise<JSType.String>>]
    private static partial Task<string> DeriveKey(byte[] credential, byte[] salt, int iterations);

    private static async Task<T> DriveOperation<T>(Func<Task<T>> run)
    {
        try { return await run(); }
        catch (JSException ex) { throw new IOException(ex.Message, ex); }
    }

    [JSImport("globalThis.totpDriveExport")]
    [return: JSMarshalAs<JSType.Promise<JSType.Boolean>>]
    private static partial Task<bool> DriveExport(byte[] bytes);

    [JSImport("globalThis.totpDriveImport")]
    [return: JSMarshalAs<JSType.Promise<JSType.String>>]
    private static partial Task<string?> DriveImport();

    [JSImport("globalThis.totpDownload")]
    private static partial void Download(string name, byte[] bytes);

    [JSImport("globalThis.totpCamera")]
    [return: JSMarshalAs<JSType.Promise<JSType.String>>]
    private static partial Task<string> Camera();

    private sealed partial class BrowserStorage : IVaultStorage
    {
        public string Location => $"Browser local storage · {Origin()}";

        [JSImport("globalThis.totpOrigin")]
        private static partial string Origin();

        [JSImport("globalThis.totpRead")]
        private static partial string? ReadLocal(string id);

        [JSImport("globalThis.totpReplace")]
        [return: JSMarshalAs<JSType.Promise<JSType.Boolean>>]
        private static partial Task<bool> ReplaceLocal(string id, string? expected, string value);

        public string? Read(string id)
        {
            try { return ReadLocal(id); }
            catch (JSException) { throw new IOException("The browser blocked storage access."); }
        }

        public async ValueTask ReplaceAsync(string id, string? expected, string value)
        {
            try
            {
                if (!await ReplaceLocal(id, expected, value))
                    throw new IOException("The vault changed. Sign in again and retry.");
            }
            catch (JSException)
            {
                throw new IOException("Could not save data in the browser.");
            }
        }
    }
}
