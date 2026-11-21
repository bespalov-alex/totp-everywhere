using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using System.Globalization;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System.Runtime.InteropServices;
using SkiaSharp;
using Totp.Core;
using ZXing;
using ZXing.Common;
using FormatException = System.FormatException;

namespace Totp.App;

internal sealed partial class VaultView : UserControl
{
    private IVaultStorage storage;
    private readonly Func<string?, IVaultStorage>? selectVaultDirectory;
    private readonly Func<Task<Stream?>> capturePhoto;
    private readonly VaultKeyDeriver? keyDeriver;
    private readonly StackPanel body = new() { Spacing = 14 };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly List<(TotpAccount Account, TextBlock Code, TextBlock Remaining)> rows = [];
    private readonly Dictionary<int, ImportPage> pending = [];
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private Vault? vault;
    private bool busy;

    public VaultView(IVaultStorage storage, Func<Task<Stream?>> capturePhoto, VaultKeyDeriver? keyDeriver,
        Func<string?, IVaultStorage>? selectVaultDirectory)
    {
        this.storage = storage;
        this.capturePhoto = capturePhoto;
        this.keyDeriver = keyDeriver;
        this.selectVaultDirectory = selectVaultDirectory;
        using var icon = AssetLoader.Open(App.IconUri);
        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 12,
            Children =
            {
                new Image { Source = new Bitmap(icon), Width = 44, Height = 44 },
                new TextBlock
                {
                    Text = "TOTP Everywhere", FontSize = 28, FontWeight = FontWeight.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center
                }
            }
        };
        Background = new SolidColorBrush(Color.FromArgb(215, 20, 30, 44));
        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                MaxWidth = 600, Margin = new Thickness(24, 36), Spacing = 20,
                Children =
                {
                    header, body, status
                }
            }
        };
        timer.Tick += (_, _) => RefreshCodes();
        AttachedToVisualTree += (_, _) => timer.Start();
        DetachedFromVisualTree += (_, _) => { timer.Stop(); Lock(); };
        ShowLogin();
    }

    private void ShowLogin()
    {
        body.Children.Clear();
        var login = new TextBox { Watermark = "Username", MaxLength = 128 };
        var password = Password("Password");
        body.Children.Add(new TextBlock { Text = "Local code vault" });
        body.Children.Add(login);
        body.Children.Add(password);
        body.Children.Add(Buttons(
            Action("Sign in", () => Open(login, password, false)),
            Action("Register", () => Open(login, password, true)),
            Action("Import vault", ShowTransfers),
            Action("Storage settings", () => ShowStorageSettings())));
    }

    private async Task Open(TextBox login, TextBox password, bool register)
    {
        var value = password.Text ?? "";
        password.Clear();
        vault = await Vault.OpenAsync(storage, login.Text ?? "", value, register, keyDeriver);
        ShowAccounts();
    }

    private void ShowAccounts()
    {
        rows.Clear();
        body.Children.Clear();
        body.Children.Add(Buttons(
            Action("Add account", () => ShowAddAccount()),
            Action("Transfer vault", ShowTransfers),
            Action("Storage settings", () => ShowStorageSettings()),
            Action("Change password", () => ShowPasswordChange()),
            Action("Sign out", () => { Lock(); ShowLogin(); })));

        foreach (var account in vault!.Accounts)
        {
            var code = new TextBlock { FontSize = 28, FontWeight = FontWeight.SemiBold };
            var remaining = new TextBlock { Opacity = .75 };
            rows.Add((account, code, remaining));
            body.Children.Add(new Border
            {
                Padding = new Thickness(16), CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(Color.FromArgb(195, 42, 60, 78)),
                Child = new StackPanel
                {
                    Spacing = 5,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = string.IsNullOrEmpty(account.Issuer) ? account.Name : $"{account.Issuer} · {account.Name}",
                            TextWrapping = TextWrapping.Wrap
                        },
                        code, remaining,
                        Buttons(Action("Edit", () => ShowAccountEditor(account)),
                            Action("Remove", () => ShowRemoveAccount(account)))
                    }
                }
            });
        }
        if (rows.Count == 0)
            body.Children.Add(new TextBlock { Text = "Add your first account." });
        RefreshCodes();
    }

    private void ShowAddAccount()
    {
        rows.Clear();
        body.Children.Clear();
        body.Children.Add(Heading("Add an account"));
        body.Children.Add(new TextBlock
        {
            Text = "Scan or upload a Google Authenticator export or a service setup QR code.",
            TextWrapping = TextWrapping.Wrap
        });
        if (pending.Count > 0)
            body.Children.Add(new TextBlock
            {
                Text = $"Google Authenticator export: {pending.Count} of {pending.Values.First().BatchSize} QR codes."
            });
        body.Children.Add(Buttons(
            Action("Upload QR image", ImportFile),
            Action("Scan QR with camera", ImportCamera),
            Action("Enter a secret key", () => ShowAccountEditor()),
            Action("Back", () => ShowAccounts())));
    }

    private void ShowAccountEditor(TotpAccount? current = null)
    {
        rows.Clear();
        body.Children.Clear();
        var issuer = new TextBox { Watermark = "Service (optional)", Text = current?.Issuer };
        var name = new TextBox { Watermark = "Account name", Text = current?.Name };
        var secret = Password(current is null ? "Base32 secret key" : "New secret key (optional)");
        var algorithm = new ComboBox
        {
            ItemsSource = new[] { "SHA-1", "SHA-256", "SHA-512" },
            SelectedIndex = (int)(current?.Algorithm ?? TotpAlgorithm.Sha1)
        };
        var digits = new ComboBox { ItemsSource = new[] { 6, 8 }, SelectedIndex = current?.Digits == 8 ? 1 : 0 };
        var period = new TextBox { Text = (current?.Period ?? 30).ToString(CultureInfo.InvariantCulture), MaxLength = 4 };
        body.Children.Add(Heading(current is null ? "Enter a secret key" : "Edit account"));
        foreach (var control in new Control[] { issuer, name, secret, new TextBlock { Text = "Algorithm" },
                     algorithm, new TextBlock { Text = "Code digits" }, digits,
                     new TextBlock { Text = "Period in seconds" }, period })
            body.Children.Add(control);

        body.Children.Add(Buttons(
            Action("Save", async () =>
            {
                var accountName = name.Text?.Trim();
                if (string.IsNullOrWhiteSpace(accountName))
                    throw new ArgumentException("Enter an account name.");
                if (!int.TryParse(period.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ||
                    seconds is < 1 or > 3600)
                    throw new ArgumentException("Enter a period from 1 to 3600 seconds.");

                var value = secret.Text ?? "";
                var key = current is not null && string.IsNullOrWhiteSpace(value)
                    ? current.Secret : QrImport.NormalizeSecret(value);
                var updated = new TotpAccount(issuer.Text?.Trim() ?? "", accountName, key,
                    (TotpAlgorithm)algorithm.SelectedIndex, (int)digits.SelectedItem!, seconds);
                if (current is null)
                {
                    if (await vault!.AddAsync([updated]) == 0)
                        throw new InvalidOperationException("This account is already in the vault.");
                }
                else
                    await vault!.UpdateAccountAsync(current, updated);

                secret.Clear();
                ShowAccounts();
                status.Text = "Account saved.";
            }),
            Action("Back", () =>
            {
                if (current is null)
                    ShowAddAccount();
                else
                    ShowAccounts();
            })));
    }

    private void ShowRemoveAccount(TotpAccount account)
    {
        rows.Clear();
        body.Children.Clear();
        body.Children.Add(Heading("Remove account?"));
        body.Children.Add(new TextBlock { Text = account.Name, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(Buttons(
            Action("Remove account", async () =>
            {
                await vault!.UpdateAccountAsync(account, null);
                ShowAccounts();
                status.Text = "Account removed.";
            }),
            Action("Cancel", () => ShowAccounts())));
    }

    private void ShowStorageSettings()
    {
        rows.Clear();
        body.Children.Clear();
        body.Children.Add(Heading("Storage location"));
        body.Children.Add(new SelectableTextBlock { Text = storage.Location, TextWrapping = TextWrapping.Wrap });
        if (selectVaultDirectory is not null)
        {
            body.Children.Add(new TextBlock
            {
                Text = "Changing folders signs you out. Existing vault files stay in their original folder.",
                TextWrapping = TextWrapping.Wrap
            });
            body.Children.Add(Buttons(Action("Choose folder", ChangeStorageDirectory),
                Action("Use default folder", () => SelectStorage(null))));
        }
        else
            body.Children.Add(new TextBlock
            {
                Text = OperatingSystem.IsBrowser()
                    ? "This browser stores the vault for this site and profile."
                    : "This device stores the vault in the app’s private directory.",
                TextWrapping = TextWrapping.Wrap
            });
        body.Children.Add(Action("Back", () =>
        {
            if (vault is null)
                ShowLogin();
            else
                ShowAccounts();
        }));
    }

    private async Task ChangeStorageDirectory()
    {
        var folders = await TopLevel.GetTopLevel(this)!.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = "Choose the vault folder" });
        if (folders.Count == 0)
            return;
        using var folder = folders[0];
        SelectStorage(folder.TryGetLocalPath() ?? throw new IOException("Choose a local filesystem folder."));
    }

    private void SelectStorage(string? directory)
    {
        // An unlocked vault retains its original storage instance until the session ends.
        storage = selectVaultDirectory!(directory);
        Lock();
        ShowLogin();
        status.Text = $"Storage location: {storage.Location}";
    }

    private static TextBlock Heading(string text) =>
        new() { Text = text, FontSize = 20, FontWeight = FontWeight.SemiBold };

    private void ShowPasswordChange()
    {
        rows.Clear();
        body.Children.Clear();
        var oldPassword = Password("Current password");
        var newPassword = Password("New password");
        var confirmation = Password("Confirm new password");
        body.Children.Add(oldPassword);
        body.Children.Add(newPassword);
        body.Children.Add(confirmation);
        body.Children.Add(Buttons(
            Action("Save", async () =>
            {
                if (newPassword.Text != confirmation.Text)
                    throw new ArgumentException("The new passwords do not match.");
                var oldValue = oldPassword.Text ?? "";
                var newValue = newPassword.Text ?? "";
                oldPassword.Clear();
                newPassword.Clear();
                confirmation.Clear();
                await vault!.ChangePasswordAsync(oldValue, newValue);
                ShowAccounts();
                status.Text = "Password changed.";
            }),
            Action("Back", () => { ShowAccounts(); return Task.CompletedTask; })));
    }

    private async Task ImportFile() => await ImportScannedAccount(await ReadPickedQr());

    private async Task ImportCamera() => await ImportScannedAccount(await ReadCameraQr());

    private async Task ImportScannedAccount(string? text)
    {
        if (text is null)
            return;
        if (text.StartsWith(VaultTransfer.QrPrefix, StringComparison.Ordinal))
            ShowTransferCredentials(text);
        else
            await ImportAsync(QrImport.Parse(text));
    }

    private async Task<string?> ReadPickedQr()
    {
        var picker = TopLevel.GetTopLevel(this)!.StorageProvider;
        var files = await picker.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a QR image",
            FileTypeFilter = [FilePickerFileTypes.ImageAll]
        });
        if (files.Count == 0)
            return null;
        using var file = files[0];
        await using var stream = await file.OpenReadAsync();
        return await ReadQrImage(stream);
    }

    private async Task<string?> ReadCameraQr()
    {
        await using var stream = await capturePhoto();
        return stream is null ? null : await ReadQrImage(stream);
    }

    private static async Task<string> ReadQrImage(Stream stream)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(chunk.AsMemory())) != 0)
        {
            if (buffer.Length + count > 20 * 1024 * 1024)
                throw new FormatException("The image exceeds 20 MB.");
            buffer.Write(chunk, 0, count);
        }
        buffer.Position = 0;
        using var codec = SKCodec.Create(buffer) ?? throw new FormatException("Could not read the image.");
        var width = codec.Info.Width;
        var height = codec.Info.Height;
        if (width <= 0 || height <= 0 || (long)width * height > 20_000_000)
            throw new FormatException("The image exceeds 20 megapixels.");

        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        if (codec.GetPixels(bitmap.Info, bitmap.GetPixels()) != SKCodecResult.Success)
            throw new FormatException("Could not read the image.");
        var pixels = new byte[width * height * 4];
        Marshal.Copy(bitmap.GetPixels(), pixels, 0, pixels.Length);
        // Composite transparency onto white so transparent QR backgrounds remain scannable.
        var grayscale = new byte[width * height];
        for (int pixel = 0, offset = 0; pixel < grayscale.Length; pixel++, offset += 4)
        {
            var intensity = (pixels[offset] * 77 + pixels[offset + 1] * 150 + pixels[offset + 2] * 29) >> 8;
            var alpha = pixels[offset + 3];
            grayscale[pixel] = (byte)((intensity * alpha + 255 * (255 - alpha)) / 255);
        }
        var luminance = new PlanarYUVLuminanceSource(grayscale, width, height, 0, 0, width, height, false);
        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = true,
            Options = new DecodingOptions { PossibleFormats = [BarcodeFormat.QR_CODE], TryHarder = true }
        };
        var result = reader.Decode(luminance) ?? throw new FormatException("No QR code was found in the image.");
        return result.Text;
    }

    private async Task ImportAsync(ImportPage page)
    {
        // Commit the migration once every page in the batch has been collected.
        var pages = new[] { page };
        if (page.BatchSize > 1)
        {
            if (pending.Values.FirstOrDefault() is { } first &&
                (first.BatchId != page.BatchId || first.BatchSize != page.BatchSize))
                pending.Clear();
            pending[page.BatchIndex] = page;
            if (pending.Count != page.BatchSize)
            {
                status.Text = $"Received {pending.Count} of {page.BatchSize} QR codes. Scan or upload the remaining codes.";
                return;
            }
            pages = pending.Values.OrderBy(part => part.BatchIndex).ToArray();
        }
        var added = await vault!.AddAsync(pages.SelectMany(part => part.Accounts));
        if (page.BatchSize > 1)
            pending.Clear();
        ShowAccounts();
        status.Text = $"Added: {added}. Unsupported entries skipped: {pages.Sum(part => part.Skipped)}.";
    }

    private void RefreshCodes()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (account, code, remaining) in rows)
        {
            var seconds = account.Period - now.ToUnixTimeSeconds() % account.Period;
            var text = $"Refreshes in {seconds}s";
            if (remaining.Text == text)
                continue;
            code.Text = account.CodeAt(now);
            remaining.Text = text;
        }
    }

    private void Lock()
    {
        transferQrImage?.Dispose();
        transferQrImage = null;
        vault?.Dispose();
        vault = null;
        rows.Clear();
        pending.Clear();
    }

    private static TextBox Password(string placeholder) =>
        new() { Watermark = placeholder, PasswordChar = '●', MaxLength = 1024 };

    private static WrapPanel Buttons(params Button[] buttons)
    {
        var panel = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var button in buttons)
        {
            button.Margin = new Thickness(0, 0, 8, 8);
            panel.Children.Add(button);
        }
        return panel;
    }

    private Button Action(string label, System.Action run) => Action(label, () =>
    {
        run();
        return Task.CompletedTask;
    });

    private Button Action(string label, Func<Task> run)
    {
        var button = new Button { Content = label };
        button.Click += async (_, _) =>
        {
            if (busy)
                return;
            busy = true;
            status.Text = "";
            try
            {
                await run();
            }
            catch (Exception error) when (error is FormatException or ArgumentException or CryptographicException or
                InvalidOperationException or OverflowException or IOException or JsonException or UnauthorizedAccessException)
            {
                status.Text = error switch
                {
                    Google.Protobuf.InvalidProtocolBufferException => "The Google Authenticator export is damaged.",
                    JsonException => "The vault is damaged.",
                    _ => error.Message
                };
            }
            finally
            {
                busy = false;
            }
        };
        return button;
    }
}
