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
            throw new InvalidOperationException("The camera requires a physical iOS device.");

        if (!await AVCaptureDevice.RequestAccessForMediaTypeAsync(AVAuthorizationMediaType.Video))
            throw new InvalidOperationException("Allow camera access in iOS Settings.");

        var presenter = Window?.RootViewController
            ?? throw new InvalidOperationException("Could not open the camera.");
        while (presenter.PresentedViewController is { } presented)
            presenter = presented;

        // Bridge the native camera callbacks to a single asynchronous capture result.
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
                Finish(null, new IOException("Could not read the camera image."));
            else
                Finish(new MemoryStream(jpeg.ToArray(), writable: false));
        };
        presenter.PresentViewController(picker, true, null);
        return await result.Task;
    }
}
