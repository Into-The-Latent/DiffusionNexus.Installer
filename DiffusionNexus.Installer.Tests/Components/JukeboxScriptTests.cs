using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Components;

/// <summary>
/// wwwroot/js/jukebox.js is this app's only JavaScript. Nothing compiles it and no bUnit test can
/// execute it -- the component tests mock the module away -- so the one bug it has already had
/// went unnoticed by the whole suite. This is the compiler it lacks, in the same spirit as
/// <see cref="StylesheetTests"/>.
/// </summary>
public class JukeboxScriptTests
{
    private static string Read(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DiffusionNexus.Installer.slnx")))
            dir = dir.Parent;
        var root = dir?.FullName ?? throw new InvalidOperationException("Repo root not found above the test output folder.");
        return File.ReadAllText(Path.Combine(new[] { root }.Concat(parts).ToArray()));
    }

    private static string Script() => Read("DiffusionNexus.Installer.Electron", "wwwroot", "js", "jukebox.js");

    private static string Stylesheet() => Read("DiffusionNexus.Installer.Electron", "wwwroot", "app.css");

    [Fact]
    public void The_opening_scroll_is_instant_and_not_auto()
    {
        // `auto` reads like "no animation" and is the opposite: per CSSOM-View it means "use the
        // element's computed scroll-behavior", and .jukebox-track sets `scroll-behavior: smooth`.
        // The first version of this line shipped `auto` and therefore animated -- the strip slid
        // visibly from the last tile back to the first on every entry to the screen, and the
        // ResizeObserver's first callback read the position being left rather than the one being
        // reached, so both arrows showed the wrong state for the length of the animation.
        var script = Script();

        var opening = Regex.Match(script, @"scrollTo\(\{\s*left:\s*0,\s*behavior:\s*'(?<how>\w+)'");
        opening.Success.Should().BeTrue("the observer opens the strip at its first tile");
        opening.Groups["how"].Value.Should().Be("instant");

        // The half that makes `auto` wrong. If this ever goes, the rule above can be revisited --
        // but they have to be read together, which is why they are asserted together.
        var track = Regex.Match(Stylesheet(), @"\.jukebox-track\s*\{(?<body>[^}]*)\}").Groups["body"].Value;
        track.Should().Contain("scroll-behavior: smooth");
    }

    [Fact]
    public void Paging_stays_smooth()
    {
        // The other call is the one that SHOULD animate: it is a journey, not a starting position.
        Script().Should().Contain("behavior: 'smooth'");
    }

    [Fact]
    public void Edge_reports_are_sent_only_when_they_change()
    {
        // A smooth scroll fires `scroll` dozens of times and the edge state turns over at most
        // twice in the whole of it. Without the guard every frame is a round trip over the
        // SignalR circuit.
        var script = Script();

        script.Should().Contain("last.atStart === next.atStart && last.atEnd === next.atEnd");
        script.Should().Contain("invokeMethodAsync('OnEdgesChanged'");
    }

    [Fact]
    public void Observing_hands_back_something_that_can_be_detached()
    {
        // The scroll listener and the ResizeObserver hold a reference back to the .NET component.
        // Welcome.razor calls dispose() on this handle; if the handle stops carrying one, the
        // leak is silent.
        Script().Should().Contain("dispose()").And.Contain("removeEventListener('scroll'").And.Contain("resize.disconnect()");
    }
}
