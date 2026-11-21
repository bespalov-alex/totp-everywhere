using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Totp.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        App.CapturePhoto = Capture;
        AppBuilder.Configure<App>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
    }

    private static async Task<Stream?> Capture()
    {
        var camera = Environment.GetEnvironmentVariable("TOTP_CAMERA");
        const string homebrewFfmpeg = "/opt/homebrew/bin/ffmpeg";
        var start = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsMacOS() && File.Exists(homebrewFfmpeg) ? homebrewFfmpeg : "ffmpeg",
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        };
        string[] input = OperatingSystem.IsWindows() ? ["-f", "dshow", "-i", "video=" + (camera ??
            throw new InvalidOperationException("Задайте имя камеры в переменной TOTP_CAMERA."))]
            : OperatingSystem.IsMacOS() ? ["-f", "avfoundation", "-framerate", "30", "-i", (camera ?? "0") + ":none"]
            : ["-f", "v4l2", "-i", camera ?? "/dev/video0"];
        string[] arguments =
        [
            "-nostdin", "-hide_banner", "-loglevel", "error", ..input,
            "-ss", "1", "-frames:v", "1", "-f", "image2pipe", "-vcodec", "png", "pipe:1"
        ];
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Win32Exception)
        {
            throw new InvalidOperationException("Установите FFmpeg и добавьте его в PATH для работы камеры.");
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var photo = new MemoryStream();
        try
        {
            await Task.WhenAll(process.StandardOutput.BaseStream.CopyToAsync(photo, timeout.Token),
                process.StandardError.ReadToEndAsync(timeout.Token), process.WaitForExitAsync(timeout.Token));
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            throw new IOException("Камера не ответила вовремя.");
        }
        if (process.ExitCode != 0 || photo.Length == 0)
            throw new IOException("Не удалось снять кадр. Проверьте разрешения и выбранное устройство.");
        return new MemoryStream(photo.ToArray(), writable: false);
    }
}
