namespace DiffusionNexus.Installer.Electron.Services;

/// <summary>
/// The two calls <see cref="AppUpdateChecker"/> makes on Electron's auto-updater. A seam only
/// because <c>Electron.AutoUpdater</c> is a static that talks to a socket no test has.
/// </summary>
public interface IAppUpdaterShell
{
    /// <summary>False outside the Electron shell (plain <c>dotnet run</c>, tests): there is no updater to talk to.</summary>
    bool IsAvailable { get; }

    void SetAllowPrerelease(bool allow);

    /// <summary>
    /// electron-builder's <c>app-update.yml</c> as shipped with this install, or null when there
    /// is none (an unpackaged run). It names the repository the releases live in.
    /// </summary>
    string? UpdateConfigPath { get; }

    /// <summary>
    /// Points the updater at another config file; it rebuilds its provider on the next check.
    /// Throws when the message could not be sent.
    /// </summary>
    Task SetUpdateConfigPathAsync(string path);

    Task CheckForUpdatesAsync();
}

/// <inheritdoc />
public sealed class ElectronAppUpdaterShell : IAppUpdaterShell
{
    public bool IsAvailable => ElectronHost.IsActive;

    public void SetAllowPrerelease(bool allow) => ElectronNET.API.Electron.AutoUpdater.AllowPrerelease = allow;

    // The .NET output is packaged as extraResources under resources/bin, and electron-builder
    // writes app-update.yml into resources/ itself -- one folder up from here.
    public string? UpdateConfigPath
    {
        get
        {
            var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "app-update.yml"));
            return File.Exists(path) ? path : null;
        }
    }

    private const string SetConfigPathMessage = "autoUpdater-updateConfigPath-set";

    // ElectronNET 0.5.2 ships the JavaScript half of this setter (.electron/api/autoUpdater.js
    // handles the message above) but its C# property is get-only, and the socket every other
    // setter emits on is internal. Hence reflection. BridgeEmit is resolved by a test, so a
    // package bump that moves it fails the suite rather than a tester's update, and the caller
    // falls back to the plain check when this throws.
    //
    // There is no reading the value back to confirm it: electron-updater's updateConfigPath is
    // a setter with no getter, so ElectronNET's get-only property always answers "". That the
    // emit reaches the main process was checked once against a live run, by sending
    // allowDowngrade-set this way and reading AllowDowngrade back through the public getter.
    public Task SetUpdateConfigPathAsync(string path) => EmitAsync(SetConfigPathMessage, path);

    /// <summary>Sends one of the bridge's setter messages over ElectronNET's internal socket.</summary>
    public static Task EmitAsync(string message, object value)
    {
        var (socket, emit) = BridgeEmit() ?? throw new InvalidOperationException("ElectronNET's bridge socket was not found.");
        return (Task)emit.Invoke(socket.GetValue(null), [message, new object[] { value }])!;
    }

    /// <summary>The internal <c>BridgeConnector.Socket</c> and its <c>Emit(string, object[])</c>, or null when ElectronNET no longer has them.</summary>
    public static (System.Reflection.PropertyInfo Socket, System.Reflection.MethodInfo Emit)? BridgeEmit()
    {
        const System.Reflection.BindingFlags anyStatic =
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;

        var socket = typeof(ElectronNET.API.AutoUpdater).Assembly.GetType("ElectronNET.API.BridgeConnector")?.GetProperty("Socket", anyStatic);
        var emit = socket?.PropertyType.GetMethod("Emit", [typeof(string), typeof(object[])]);
        return socket is not null && emit is not null && typeof(Task).IsAssignableFrom(emit.ReturnType) ? (socket, emit) : null;
    }

    public Task CheckForUpdatesAsync() => ElectronNET.API.Electron.AutoUpdater.CheckForUpdatesAsync();
}
