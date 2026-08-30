using DiffusionNexus.Installer.Core;
using DiffusionNexus.Installer.Core.Host;
using DiffusionNexus.Installer.Electron.Services;
using DiffusionNexus.Installer.SDK.Catalog;
using DiffusionNexus.Installer.SDK.Services;
using DiffusionNexus.Installer.SDK.Services.Installation;
using DiffusionNexus.Installer.SDK.Services.Settings;
using ElectronNET.API;
using ElectronNET.API.Entities;

// NOTE: DiffusionNexus.Installer.Electron.Components is deliberately NOT imported: its `App`
// component collides with ElectronNET.API.App. The Blazor root is fully qualified below instead.
using BlazorApp = DiffusionNexus.Installer.Electron.Components.App;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddElectron();
builder.Services.AddSingleton<UpdaterLog>();

// SDK core services. IGitService/IPythonService/IProcessRunner are not registered by
// AddInstallationServices -- the host owns them, exactly as the 2.x app does.
builder.Services.AddSingleton<IProcessRunner, ProcessRunner>();
builder.Services.AddSingleton<IGitService, GitService>();
builder.Services.AddSingleton<IPythonService, PythonService>();
builder.Services.AddInstallationServices();
builder.Services.AddSingleton<IInstallationOrchestrator, InstallationOrchestrator>();
builder.Services.AddDiffusionNexusUserSettings();

builder.Services.AddDiffusionNexusCatalog(options =>
{
    // The installer ships the catalog it was built against, so a machine with no catalog yet
    // still has a full workload list before it ever reaches the network.
    var assembly = typeof(Program).Assembly;
    options.EmbeddedArchive = () => assembly.GetManifestResourceStream("catalog.zip")!;
    options.EmbeddedManifest = () => assembly.GetManifestResourceStream("manifest.json")!;

    // Point at a catalog checkout to test content changes before publishing them. A missing
    // path warns and falls back rather than failing to start.
    options.LocalOverridePath = Environment.GetEnvironmentVariable("DIFFUSIONNEXUS_CATALOG_PATH");
});

builder.Services.AddInstallerCore();
builder.Services.AddSingleton<ModalPromptService>();
builder.Services.AddSingleton<IUserPrompt>(sp => sp.GetRequiredService<ModalPromptService>());
builder.Services.AddSingleton<IFolderPicker, ElectronFolderPicker>();

// The Electron shell is only spun up when the app is launched through Electron; running the
// project directly still serves the Blazor UI in a browser, which keeps plain `dotnet run`
// useful for UI work without paying Electron startup on every iteration.
builder.UseElectron(args, async (IServiceProvider services) =>
{
    var options = new BrowserWindowOptions
    {
        Title = "DiffusionNexus Installer",
        Width = 1100,
        Height = 800,
        MinWidth = 900,
        MinHeight = 650,
        Center = true,

        // Blazor negotiates its own SignalR circuit after first paint; without this the window
        // is shown before the circuit is live and the user sees a blank frame.
        IsRunningBlazor = true,

        // Shown from OnReadyToShow instead, so the window never appears unpainted.
        Show = false
    };

    // macOS puts the menu in the system bar, so there is no in-window bar to hide there.
    if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
    {
        options.AutoHideMenuBar = true;
    }

    var window = await Electron.WindowManager.CreateWindowAsync(options);
    window.OnReadyToShow += () => window.Show();

    // Wired once, here, rather than from a component -- see UpdaterLog for why.
    var log = services.GetRequiredService<UpdaterLog>();

    Electron.AutoUpdater.OnCheckingForUpdate += () => log.Append("Checking for updates...");
    Electron.AutoUpdater.OnUpdateNotAvailable += _ => log.Append("No update available - this is the latest version.");
    Electron.AutoUpdater.OnUpdateAvailable += info => log.Append($"Update available: {info.Version}. Downloading...");
    Electron.AutoUpdater.OnDownloadProgress += p => log.Append($"Downloading... {p.Percent:F0}%");
    Electron.AutoUpdater.OnError += error => log.Append($"Updater error: {error}");
    Electron.AutoUpdater.OnUpdateDownloaded += info =>
    {
        log.Append($"Update {info.Version} downloaded and ready to install.");
        log.MarkUpdateReady();
    };

    // Check once at startup. An installer is a short-lived, occasionally-run app: if it waited
    // for the user to ask, most installs would simply never update. Fire-and-forget so a slow
    // or unreachable GitHub cannot delay the window appearing.
    _ = Task.Run(async () =>
    {
        try
        {
            await Electron.AutoUpdater.CheckForUpdatesAsync();
        }
        catch (Exception ex)
        {
            log.Append($"Startup update check failed: {ex.Message}");
        }
    });
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<BlazorApp>()
    .AddInteractiveServerRenderMode();

app.Run();
