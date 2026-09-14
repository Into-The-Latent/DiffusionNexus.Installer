using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Components;

/// <summary>
/// wwwroot/js/install-log.js, guarded the same way <see cref="JukeboxScriptTests"/> guards the
/// other script: nothing compiles it, and the component test can only prove that the module is
/// asked for — bUnit never runs a line of it. Each assertion below is a way this file has to
/// behave that is invisible to every other test in the suite.
/// </summary>
public class InstallLogScriptTests
{
    private static string Script()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DiffusionNexus.Installer.slnx")))
            dir = dir.Parent;
        var root = dir?.FullName ?? throw new InvalidOperationException("Repo root not found above the test output folder.");
        return File.ReadAllText(Path.Combine(root, "DiffusionNexus.Installer.Electron", "wwwroot", "js", "install-log.js"));
    }

    [Fact]
    public void It_watches_the_text_node_and_not_only_the_children()
    {
        // Blazor rewrites the single text node inside the <pre> rather than replacing children, so
        // a childList-only observer never fires and the log silently stops following.
        var observe = Regex.Match(Script(), @"observer\.observe\([^)]*\{(?<options>[^}]*)\}");

        observe.Success.Should().BeTrue("the module observes the log box for changes");
        observe.Groups["options"].Value.Should().Contain("characterData: true").And.Contain("subtree: true");
    }

    [Fact]
    public void Scrolling_up_stops_the_log_following()
    {
        // Reading something further up is deliberate; yanking the view back down on the next line
        // would make the log unreadable exactly when someone is trying to read it.
        var script = Script();

        script.Should().MatchRegex(@"addEventListener\('scroll'",
            "the module has to notice the reader scrolling away from the bottom");
        script.Should().MatchRegex(@"if \(pinned\) box\.scrollTop = box\.scrollHeight",
            "and must only scroll while the reader is still at the bottom");
    }

    [Fact]
    public void The_handle_detaches_everything_it_attached()
    {
        // The stage is rebuilt on every circuit reconnect. A MutationObserver left running against
        // a detached element, once per reconnect, is a leak nothing else would surface.
        // The LAST dispose() in the file: the first one is the empty handle returned for a missing
        // box, which would pass an "is it empty?" assertion by being empty.
        var dispose = Regex.Matches(Script(), @"dispose\(\)\s*\{(?<body>[^}]*)\}")
            .Select(m => m.Groups["body"].Value).Last();

        dispose.Should().Contain("observer.disconnect()").And.Contain("removeEventListener('scroll'");
    }

    [Fact]
    public void A_missing_box_is_survivable()
    {
        // The .NET side passes an ElementReference that a teardown mid-import can leave empty.
        Script().Should().Contain("if (!box) return { dispose() { } };");
    }
}
