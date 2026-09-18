namespace DiffusionNexus.Installer.Electron.Services;

/// <summary>
/// Branding assets the running host hands to Electron. The csproj comment on the Icons block
/// covers the packaged binaries; this is the one place the WINDOW learns its icon.
/// </summary>
public static class AppBranding
{
    /// <summary>
    /// Absolute path of the window icon. Without an explicit icon Electron uses the icon of the
    /// exe that hosts the window: the packaged build's exe is rebranded by electron-builder, so
    /// it shows the product icon, but a Debug session runs the stock <c>electron.exe</c> from
    /// node_modules and shows the Electron atom instead. Passing the file makes both the same.
    /// Absolute because Electron resolves a relative path against its own cwd, which differs
    /// between the two; <see cref="AppContext.BaseDirectory"/> is the .NET output in Debug and
    /// <c>resources/bin</c> when packaged, and the Content item ships the .ico under both.
    /// </summary>
    public static string WindowIconPath { get; } =
        Path.Combine(AppContext.BaseDirectory, "Assets", "Branding", "app.ico");
}
