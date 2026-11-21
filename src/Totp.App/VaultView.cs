using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System.Runtime.InteropServices;
using SkiaSharp;
using Totp.Core;
using ZXing;
using ZXing.Common;
using FormatException = System.FormatException;

namespace Totp.App;

internal sealed class VaultView : UserControl
{
    private readonly IVaultStorage storage;
    private readonly Func<Task<Stream?>> capturePhoto;
    private readonly StackPanel body = new() { Spacing = 14 };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly List<(TotpAccount Account, TextBlock Code, TextBlock Remaining)> rows = [];
    private readonly Dictionary<int, ImportPage> pending = [];
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private Vault? vault;
    private bool busy;

    public VaultView(IVaultStorage storage, Func<Task<Stream?>> capturePhoto)
    {
        this.storage = storage;
        this.capturePhoto = capturePhoto;
        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                MaxWidth = 600, Margin = new Thickness(24, 36), Spacing = 20,
                Children =
                {
                    new TextBlock { Text = "TOTP Everywhere", FontSize = 32, FontWeight = FontWeight.SemiBold },
                    body, status
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
        var login = new TextBox { Watermark = "Логин", MaxLength = 128 };
        var password = Password("Пароль");
        body.Children.Add(new TextBlock { Text = "Локальное хранилище кодов" });
        body.Children.Add(login);
        body.Children.Add(password);
        body.Children.Add(Buttons(
            Action("Войти", () => Open(login, password, false)),
            Action("Регистрация", () => Open(login, password, true))));
    }

    private async Task Open(TextBox login, TextBox password, bool register)
    {
        var value = password.Text ?? "";
        password.Clear();
        vault = await Vault.OpenAsync(storage, login.Text ?? "", value, register);
        ShowAccounts();
    }

    private void ShowAccounts()
    {
        rows.Clear();
        body.Children.Clear();
        body.Children.Add(Buttons(
            Action("Загрузить QR", ImportFile),
            Action("Камера", ImportCamera),
            Action("Сменить пароль", () => { ShowPasswordChange(); return Task.CompletedTask; }),
            Action("Выйти", () => { Lock(); ShowLogin(); return Task.CompletedTask; })));

        foreach (var account in vault!.Accounts)
        {
            var code = new TextBlock { FontSize = 28, FontWeight = FontWeight.SemiBold };
            var remaining = new TextBlock { Opacity = .75 };
            rows.Add((account, code, remaining));
            body.Children.Add(new Border
            {
                Padding = new Thickness(16), CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(Color.FromRgb(38, 43, 54)),
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
                        code, remaining
                    }
                }
            });
        }
        if (rows.Count == 0)
            body.Children.Add(new TextBlock { Text = "Добавьте первый аккаунт по QR-коду." });
        RefreshCodes();
    }

    private void ShowPasswordChange()
    {
        rows.Clear();
        body.Children.Clear();
        var oldPassword = Password("Старый пароль");
        var newPassword = Password("Новый пароль");
        var confirmation = Password("Повторите новый пароль");
        body.Children.Add(oldPassword);
        body.Children.Add(newPassword);
        body.Children.Add(confirmation);
        body.Children.Add(Buttons(
            Action("Сохранить", async () =>
            {
                if (newPassword.Text != confirmation.Text)
                    throw new ArgumentException("Новые пароли не совпадают.");
                var oldValue = oldPassword.Text ?? "";
                var newValue = newPassword.Text ?? "";
                oldPassword.Clear();
                newPassword.Clear();
                confirmation.Clear();
                await vault!.ChangePasswordAsync(oldValue, newValue);
                ShowAccounts();
                status.Text = "Пароль изменён.";
            }),
            Action("Назад", () => { ShowAccounts(); return Task.CompletedTask; })));
    }

    private async Task ImportFile()
    {
        var picker = TopLevel.GetTopLevel(this)!.StorageProvider;
        var files = await picker.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Выберите изображение QR-кода",
            FileTypeFilter = [FilePickerFileTypes.ImageAll]
        });
        if (files.Count > 0)
        {
            await using var stream = await files[0].OpenReadAsync();
            await ImportImage(stream);
        }
    }

    private async Task ImportCamera()
    {
        await using var stream = await capturePhoto();
        if (stream is not null)
            await ImportImage(stream);
    }

    private async Task ImportImage(Stream stream)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(chunk.AsMemory())) != 0)
        {
            if (buffer.Length + count > 20 * 1024 * 1024)
                throw new FormatException("Размер файла превышает 20 МБ.");
            buffer.Write(chunk, 0, count);
        }
        buffer.Position = 0;
        using var codec = SKCodec.Create(buffer) ?? throw new FormatException("Не удалось прочитать изображение.");
        var width = codec.Info.Width;
        var height = codec.Info.Height;
        if (width <= 0 || height <= 0 || (long)width * height > 20_000_000)
            throw new FormatException("Разрешение изображения превышает 20 мегапикселей.");

        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        if (codec.GetPixels(bitmap.Info, bitmap.GetPixels()) != SKCodecResult.Success)
            throw new FormatException("Не удалось прочитать изображение.");
        var pixels = new byte[width * height * 4];
        Marshal.Copy(bitmap.GetPixels(), pixels, 0, pixels.Length);
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
        var result = reader.Decode(luminance) ?? throw new FormatException("QR-код на изображении не найден.");
        await ImportAsync(QrImport.Parse(result.Text));
    }

    private async Task ImportAsync(ImportPage page)
    {
        var pages = new[] { page };
        if (page.BatchSize > 1)
        {
            if (pending.Values.FirstOrDefault() is { } first &&
                (first.BatchId != page.BatchId || first.BatchSize != page.BatchSize))
                pending.Clear();
            pending[page.BatchIndex] = page;
            if (pending.Count != page.BatchSize)
            {
                status.Text = $"Получено QR-кодов: {pending.Count} из {page.BatchSize}. Загрузите остальные.";
                return;
            }
            pages = pending.Values.OrderBy(part => part.BatchIndex).ToArray();
        }
        var added = await vault!.AddAsync(pages.SelectMany(part => part.Accounts));
        if (page.BatchSize > 1)
            pending.Clear();
        ShowAccounts();
        status.Text = $"Добавлено: {added}. Пропущено неподдерживаемых: {pages.Sum(part => part.Skipped)}.";
    }

    private void RefreshCodes()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (account, code, remaining) in rows)
        {
            var seconds = account.Period - now.ToUnixTimeSeconds() % account.Period;
            var text = $"Обновление через {seconds} с";
            if (remaining.Text == text)
                continue;
            code.Text = account.CodeAt(now);
            remaining.Text = text;
        }
    }

    private void Lock()
    {
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
                InvalidOperationException or OverflowException or IOException or JsonException)
            {
                status.Text = error switch
                {
                    Google.Protobuf.InvalidProtocolBufferException => "Повреждены данные экспорта Google Authenticator.",
                    JsonException => "Хранилище повреждено.",
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
