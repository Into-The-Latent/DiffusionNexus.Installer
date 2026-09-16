using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Components;

/// <summary>
/// wwwroot/js/clipboard.js, guarded the same way <see cref="InstallLogScriptTests"/> guards the
/// log follower: nothing compiles it and bUnit never runs it, so the component test can only prove
/// that the module is asked for.
/// </summary>
public class ClipboardScriptTests
{
    private static string Script()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DiffusionNexus.Installer.slnx")))
            dir = dir.Parent;
        var root = dir?.FullName ?? throw new InvalidOperationException("Repo root not found above the test output folder.");
        return File.ReadAllText(Path.Combine(root, "DiffusionNexus.Installer.Electron", "wwwroot", "js", "clipboard.js"));
    }

    [Fact]
    public void It_exports_the_function_the_service_imports()
    {
        Script().Should().Contain("export async function copyText(text)");
    }

    [Fact]
    public void It_uses_the_async_clipboard_and_keeps_the_legacy_route_as_a_fallback()
    {
        // navigator.clipboard can be refused (an unfocused window, a policy). Without the fallback
        // the user gets a button that did nothing, which is the failure this whole feature exists
        // to end.
        var script = Script();

        script.Should().Contain("navigator.clipboard.writeText(text)");
        script.Should().Contain("document.execCommand('copy')");
        script.Should().Contain("area.remove()", "the hidden textarea must not be left in the page");
    }
}
