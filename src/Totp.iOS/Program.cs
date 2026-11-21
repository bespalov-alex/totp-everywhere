using AVFoundation;
using Avalonia;
using Avalonia.iOS;
using Foundation;
using Totp.App;
using UIKit;

internal static class Program
{
    private static void Main(string[] args) => UIApplication.Main(args, null, typeof(AppDelegate));
}

[Register("AppDelegate")]
public sealed partial class AppDelegate : AvaloniaAppDelegate<App>
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        App.CapturePhoto = Capture;
        return base.CustomizeAppBuilder(builder);
    }

    private async Task<Stream?> Capture()
    {
        if (!UIImagePickerController.IsSourceTypeAvailable(UIImagePickerControllerSourceType.Camera))
            throw new InvalidOperationException("Камера доступна на устройстве iOS.");

        if (!await AVCaptureDevice.RequestAccessForMediaTypeAsync(AVAuthorizationMediaType.Video))
            throw new InvalidOperationException("Разрешите приложению доступ к камере в настройках iOS.");

        var presenter = Window?.RootViewController
            ?? throw new InvalidOperationException("Не удалось открыть окно камеры.");
        while (presenter.PresentedViewController is { } presented)
            presenter = presented;

        var result = new TaskCompletionSource<Stream?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var picker = new UIImagePickerController { SourceType = UIImagePickerControllerSourceType.Camera };
        void Finish(Stream? photo, Exception? error = null)
        {
            picker.DismissViewController(true, () =>
            {
                picker.Dispose();
                if (error is null)
                    result.TrySetResult(photo);
                else
                    result.TrySetException(error);
            });
        }

        picker.Canceled += (_, _) => Finish(null);
        picker.FinishedPickingMedia += (_, args) =>
        {
            using var jpeg = args.OriginalImage?.AsJPEG(0.9f);
            if (jpeg is null)
                Finish(null, new IOException("Не удалось получить изображение камеры."));
            else
                Finish(new MemoryStream(jpeg.ToArray(), writable: false));
        };
        presenter.PresentViewController(picker, true, null);
        return await result.Task;
    }
}
