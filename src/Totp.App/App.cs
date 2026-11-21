using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Themes.Fluent;
using Totp.Core;

namespace Totp.App;

public sealed class App : Application
{
    public static string DefaultVaultDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TotpEverywhere");

    public static IVaultStorage Storage { get; set; } = new FileVaultStorage(DefaultVaultDirectory);
    public static Func<string?, IVaultStorage>? SelectVaultDirectory { get; set; }
    internal static Uri IconUri { get; } = new("avares://Totp.App/Assets/icon.png");

    public static VaultKeyDeriver? KeyDeriver { get; set; }
    public static Action<string, byte[]>? DownloadFile { get; set; }

    public static Func<Task<Stream?>> CapturePhoto { get; set; } =
        () => throw new InvalidOperationException("Camera unavailable.");

    public override void Initialize()
    {
        Name = "TOTP Everywhere";
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
        if (OperatingSystem.IsMacOS())
        {
            // Supplying an application menu replaces the framework's default About item.
            var menu = new NativeMenu();
            var about = new NativeMenuItem("About TOTP Everywhere");
            about.Click += async (_, _) =>
            {
                if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
                    return;

                await new Window
                {
                    Title = "About TOTP Everywhere",
                    Width = 350, Height = 115, CanResize = false,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Content = new TextBlock
                    {
                        Text = "Local authenticator for time-based one-time passwords.",
                        Margin = new Thickness(22), TextWrapping = TextWrapping.Wrap
                    }
                }.ShowDialog(owner);
            };
            menu.Add(about);
            NativeMenu.SetMenu(this, menu);
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var view = new VaultView(Storage, CapturePhoto, KeyDeriver, SelectVaultDirectory);
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            using var icon = AssetLoader.Open(IconUri);
            desktop.MainWindow = new Window
            {
                Title = Name, Width = 660, Height = 720, Content = view, Icon = new WindowIcon(icon),
                // The fallback keeps text legible when the compositor does not support translucency.
                TransparencyLevelHint = [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Transparent],
                TransparencyBackgroundFallback = new SolidColorBrush(Color.FromRgb(23, 31, 44)),
                Background = Brushes.Transparent
            };
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime single)
            single.MainView = view;
        base.OnFrameworkInitializationCompleted();
    }
}
