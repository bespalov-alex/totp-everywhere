using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Provider;
using Android.Runtime;
using AndroidX.Core.Content;
using Avalonia.Android;
using TotpApplication = Totp.App.App;
using Totp.Core;

namespace Totp.Android;

[Activity(Label = "TOTP Everywhere", Theme = "@style/TotpTheme", Icon = "@drawable/app_icon",
    MainLauncher = true, Exported = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public sealed class MainActivity : AvaloniaMainActivity<TotpApplication>
{
    private const int CameraRequest = 197;
    private TaskCompletionSource<bool>? captureResult;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        TotpApplication.Storage = new FileVaultStorage(Path.Combine(FilesDir!.AbsolutePath, "TotpEverywhere"));
        TotpApplication.CapturePhoto = CaptureAsync;
        base.OnCreate(savedInstanceState);
    }

    private async Task<Stream?> CaptureAsync()
    {
        var directory = Path.Combine(CacheDir!.AbsolutePath, "qr-capture");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".jpg");
        using (File.Create(path)) { }

        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        captureResult = result;
        try
        {
            // Give the system camera access only to this temporary file. Full resolution helps with dense QR codes.
            using var output = FileProvider.GetUriForFile(this, PackageName + ".camera", new Java.IO.File(path));
            using var intent = new Intent(MediaStore.ActionImageCapture);
            intent.PutExtra(MediaStore.ExtraOutput, output);
            intent.ClipData = ClipData.NewRawUri("QR image", output);
            intent.AddFlags(ActivityFlags.GrantWriteUriPermission | ActivityFlags.GrantReadUriPermission);
#pragma warning disable CS0618 // Avalonia's Android storage provider also uses activity result callbacks.
            StartActivityForResult(intent, CameraRequest);
#pragma warning restore CS0618
            if (!await result.Task)
                return null;
            var info = new FileInfo(path);
            if (info.Length is <= 0 or > 20 * 1024 * 1024)
                throw new IOException("The camera image must be between 1 byte and 20 MB.");
            return new MemoryStream(await File.ReadAllBytesAsync(path), writable: false);
        }
        catch (ActivityNotFoundException)
        {
            throw new InvalidOperationException("No camera app is available.");
        }
        finally
        {
            captureResult = null;
            File.Delete(path);
        }
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode == CameraRequest)
            captureResult?.TrySetResult(resultCode == Result.Ok);
    }

    protected override void OnDestroy()
    {
        captureResult?.TrySetResult(false);
        base.OnDestroy();
    }
}

[Register("com.bespalov.totpeverywhere.CameraFileProvider")]
public sealed class CameraFileProvider : FileProvider;
