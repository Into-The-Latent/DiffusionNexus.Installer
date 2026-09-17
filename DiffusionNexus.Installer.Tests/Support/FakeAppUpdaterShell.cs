using DiffusionNexus.Installer.Electron.Services;

namespace DiffusionNexus.Installer.Tests.Support;

/// <summary>Records what the app update check told Electron's updater, in order.</summary>
internal sealed class FakeAppUpdaterShell : IAppUpdaterShell
{
    public List<string> Calls { get; } = [];
    public bool IsAvailable { get; set; } = true;
    public Exception? CheckFailure { get; set; }
    public string? UpdateConfigPath { get; set; }

    public void SetAllowPrerelease(bool allow) => Calls.Add($"allowPrerelease={allow}");

    public Exception? ConfigPathFailure { get; set; }

    public Task SetUpdateConfigPathAsync(string path)
    {
        Calls.Add($"configPath={path}");
        return ConfigPathFailure is null ? Task.CompletedTask : Task.FromException(ConfigPathFailure);
    }

    /// <summary>When set, the check parks here until the test completes it.</summary>
    public TaskCompletionSource? CheckGate { get; set; }

    public async Task CheckForUpdatesAsync()
    {
        Calls.Add("check");
        if (CheckGate is not null) await CheckGate.Task;
        if (CheckFailure is not null) throw CheckFailure;
    }
}
