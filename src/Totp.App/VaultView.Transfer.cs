using System.Text;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using SkiaSharp;
using Totp.Core;
using ZXing;
using ZXing.QrCode;
using ZXing.QrCode.Internal;
using FormatException = System.FormatException;

namespace Totp.App;

internal sealed partial class VaultView
{
    private static readonly FilePickerFileType TransferFileType = new("TOTP Everywhere transfer")
    {
        Patterns = ["*.json"], MimeTypes = ["application/json"], AppleUniformTypeIdentifiers = ["public.json"]
    };
    private Bitmap? transferQrImage;

    private void ShowTransfers()
    {
        rows.Clear();
        body.Children.Clear();
        body.Children.Add(Heading("Transfer vault"));
        if (vault is not null)
        {
            var transfer = vault.ExportTransfer(TransferSource());
            var qr = transfer.ToQrText();
            if (App.ExportDriveBackup is { } export)
                body.Children.Add(Action("Export to Google Drive", async () =>
                {
                    status.Text = "Connecting to Google Drive…";
                    status.Text = await export(Encoding.UTF8.GetBytes(transfer.ToFile()))
                        ? "Encrypted backup uploaded to Google Drive." : "";
                }));
            body.Children.Add(Buttons(Action("Export transfer file", () => ExportTransferFile(transfer))));
            if (qr is not null)
                body.Children.Add(Action("Show transfer QR", () => ShowTransferQr(qr)));
            else
                body.Children.Add(new TextBlock { Text = "This vault is too large for one QR code. Export it as a file.",
                    TextWrapping = TextWrapping.Wrap });
        }

        if (App.ImportDriveBackup is { } import)
            body.Children.Add(Action("Import from Google Drive", async () =>
            {
                status.Text = "Connecting to Google Drive…";
                var text = await import();
                status.Text = "";
                if (text is not null)
                    ShowTransferCredentials(text);
            }));
        body.Children.Add(Buttons(
            Action("Import transfer file", ImportTransferFile),
            Action("Scan transfer QR", () => ReadTransferQr(fromCamera: true)),
            Action("Upload transfer QR image", () => ReadTransferQr(fromCamera: false)),
            Action("Back", BackFromTransfer)));
    }

    private static string TransferSource() =>
        OperatingSystem.IsBrowser() ? "TOTP Everywhere for Web" :
        OperatingSystem.IsAndroid() ? "TOTP Everywhere for Android" :
        OperatingSystem.IsIOS() ? "TOTP Everywhere for iOS" :
        OperatingSystem.IsMacOS() ? "TOTP Everywhere for macOS" :
        OperatingSystem.IsWindows() ? "TOTP Everywhere for Windows" :
        "TOTP Everywhere for Linux";

    private async Task ExportTransferFile(VaultTransfer transfer)
    {
        const string fileName = "totp-everywhere-transfer.json";
        var bytes = Encoding.UTF8.GetBytes(transfer.ToFile());
        if (App.DownloadFile is { } download)
        {
            download(fileName, bytes);
            status.Text = "Encrypted transfer file exported.";
            return;
        }

        var picker = TopLevel.GetTopLevel(this)!.StorageProvider;
        using var file = await picker.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export encrypted vault",
            SuggestedFileName = fileName,
            DefaultExtension = "json",
            FileTypeChoices = [TransferFileType],
            ShowOverwritePrompt = true
        });
        if (file is null)
            return;
        await using (var stream = await file.OpenWriteAsync())
        {
            if (stream.CanSeek)
                stream.SetLength(0);
            await stream.WriteAsync(bytes);
        }
        status.Text = "Encrypted transfer file exported.";
    }

    private async Task ImportTransferFile()
    {
        var picker = TopLevel.GetTopLevel(this)!.StorageProvider;
        var files = await picker.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose an encrypted vault transfer",
            FileTypeFilter = [TransferFileType, FilePickerFileTypes.All]
        });
        if (files.Count == 0)
            return;

        using var file = files[0];
        await using var stream = await file.OpenReadAsync();
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(chunk.AsMemory())) != 0)
        {
            if (buffer.Length + count > VaultTransfer.MaximumFileBytes)
                throw new FormatException("The transfer exceeds the 16 MB limit.");
            buffer.Write(chunk, 0, count);
        }
        ShowTransferCredentials(Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length).TrimStart('\uFEFF'));
    }

    private async Task ReadTransferQr(bool fromCamera)
    {
        var text = fromCamera ? await ReadCameraQr() : await ReadPickedQr();
        if (text is not null)
        {
            if (!text.StartsWith(VaultTransfer.QrPrefix, StringComparison.Ordinal))
                throw new FormatException("This QR code is not an encrypted TOTP Everywhere transfer.");
            ShowTransferCredentials(text);
        }
    }

    private void ShowTransferCredentials(string text)
    {
        var transfer = VaultTransfer.Parse(text);
        rows.Clear();
        body.Children.Clear();
        body.Children.Add(Heading("Unlock transfer"));
        body.Children.Add(new TextBlock
        {
            Text = $"Version {transfer.Version} · {transfer.Source}\n{transfer.Description}\n" +
                   $"Username: {transfer.Login}\nCRC-32: {transfer.Crc32}",
            TextWrapping = TextWrapping.Wrap
        });
        var login = new TextBox { Watermark = "Transfer username", MaxLength = 128 };
        var password = Password("Transfer password");
        body.Children.Add(login);
        body.Children.Add(password);
        body.Children.Add(Buttons(Action("Unlock and import", async () =>
        {
            var value = password.Text ?? "";
            password.Clear();
            var imported = await Vault.ImportTransferAsync(storage, text, login.Text ?? "", value, keyDeriver);
            Lock();
            vault = imported;
            ShowAccounts();
            status.Text = "Vault transfer imported.";
        }), Action("Back", ShowTransfers)));
    }

    private void ShowTransferQr(string text)
    {
        var modules = new QRCodeWriter().encode(text, BarcodeFormat.QR_CODE, 0, 0,
            new Dictionary<EncodeHintType, object>
            {
                [EncodeHintType.CHARACTER_SET] = "UTF-8",
                [EncodeHintType.ERROR_CORRECTION] = ErrorCorrectionLevel.M,
                [EncodeHintType.MARGIN] = 4
            });
        // Scale the module grid with nearest-neighbor interpolation to keep edges sharp.
        using var bitmap = new SKBitmap(modules.Width, modules.Height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        for (var y = 0; y < modules.Height; y++)
            for (var x = 0; x < modules.Width; x++)
                bitmap.SetPixel(x, y, modules[x, y] ? SKColors.Black : SKColors.White);
        using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = png.AsStream();
        transferQrImage?.Dispose();
        transferQrImage = new Bitmap(stream);
        var image = new Image { Source = transferQrImage, MaxWidth = 416, MaxHeight = 416, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.None);
        body.Children.Clear();
        body.Children.Add(Heading("Encrypted transfer QR"));
        body.Children.Add(image);
        body.Children.Add(Action("Back", () =>
        {
            body.Children.Clear();
            transferQrImage?.Dispose();
            transferQrImage = null;
            ShowTransfers();
        }));
    }

    private void BackFromTransfer()
    {
        if (vault is null)
            ShowLogin();
        else
            ShowAccounts();
    }
}
