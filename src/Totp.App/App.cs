using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Totp.Core;

namespace Totp.App;

public sealed class App : Application
{
    public static IVaultStorage Storage { get; set; } = new FileVaultStorage(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TotpEverywhere"));

    public static VaultKeyDeriver? KeyDeriver { get; set; }

    public static Func<Task<Stream?>> CapturePhoto { get; set; } =
        () => throw new InvalidOperationException("Камера недоступна.");

    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var view = new VaultView(Storage, CapturePhoto, KeyDeriver);
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new Window { Title = "TOTP Everywhere", Width = 660, Height = 720, Content = view };
        else if (ApplicationLifetime is ISingleViewApplicationLifetime single)
            single.MainView = view;
        base.OnFrameworkInitializationCompleted();
    }
}
