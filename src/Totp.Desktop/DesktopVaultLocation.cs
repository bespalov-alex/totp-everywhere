using Totp.Core;

internal sealed class DesktopVaultLocation
{
    private readonly string defaultDirectory;
    private readonly string settingsFile;

    public DesktopVaultLocation(string defaultDirectory)
    {
        this.defaultDirectory = defaultDirectory;
        settingsFile = Path.Combine(defaultDirectory, "storage-location.txt");
    }

    public FileVaultStorage Open()
    {
        try
        {
            var directory = File.ReadAllText(settingsFile);
            if (Path.IsPathFullyQualified(directory))
                return new FileVaultStorage(directory);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // An absent or unreadable preference should not prevent opening the default vault.
        }

        return new FileVaultStorage(defaultDirectory);
    }

    public FileVaultStorage Select(string? directory)
    {
        var selected = Path.GetFullPath(directory ?? defaultDirectory);
        if (!Directory.Exists(selected) && directory is not null)
            throw new IOException("The selected directory is unavailable.");

        // Keep the preference in the default directory so the app can find it on the next launch.
        Directory.CreateDirectory(defaultDirectory);
        var temporary = settingsFile + "." + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, selected);
            File.Move(temporary, settingsFile, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
        return new FileVaultStorage(selected);
    }
}
