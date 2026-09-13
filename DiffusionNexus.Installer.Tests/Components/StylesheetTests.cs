using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Components;

/// <summary>
/// The stylesheet is hand-edited and merged like code but never compiled. A conflict resolution
/// once dropped a closing brace, which silently swallowed the next rule (the licences text box
/// lost its scrolling) while every component test stayed green. This is the compiler it lacks.
/// </summary>
public class StylesheetTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DiffusionNexus.Installer.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repo root not found above the test output folder.");
    }

    [Fact]
    public void every_modal_card_is_capped_to_the_window_and_scrolls()
    {
        // Review finding: the cap was opt-in (.modal-card-scroll) so MismatchModal, which lists
        // every mismatched file, kept the bug LicensesModal was fixed for.
        var css = File.ReadAllText(Path.Combine(RepoRoot(), "DiffusionNexus.Installer.Electron", "wwwroot", "app.css"));
        var card = Regex.Match(css, @"\.modal-card\s*\{(?<body>[^}]*)\}").Groups["body"].Value;
        card.Should().Contain("max-height: calc(100vh - 3rem)").And.Contain("overflow: auto").And.Contain("margin: auto");
        var backdrop = Regex.Match(css, @"\.modal-backdrop\s*\{(?<body>[^}]*)\}").Groups["body"].Value;
        backdrop.Should().Contain("align-items: flex-start").And.Contain("overflow: auto");
    }

    [Fact]
    public void the_two_card_grids_share_one_artwork_rule_instead_of_copying_it()
    {
        // Review finding: .workload-card-art / .software-card-art, their img rules and their
        // -unavailable rules were three byte-identical PAIRS. The two grids are the same tile at
        // two widths, so a change to the crop or the border had to be made twice -- and the pair
        // was free to drift apart in between with nothing to notice.
        var css = File.ReadAllText(Path.Combine(RepoRoot(), "DiffusionNexus.Installer.Electron", "wwwroot", "app.css"));

        Regex.Matches(css, @"\.card-art\s*\{").Count.Should().Be(1, "one shared artwork rule, not one per grid");
        Regex.Matches(css, @"\.card-unavailable\s*\{").Count.Should().Be(1);

        // The component-specific classes stay ON the elements as hooks, but must carry no
        // declarations of their own -- that is what re-introduces the copy.
        foreach (var duplicate in new[] { "workload-card-art", "software-card-art", "workload-card-unavailable", "software-card-unavailable" })
        {
            Regex.IsMatch(css, $@"\.{duplicate}\s*\{{").Should().BeFalse(
                $".{duplicate} must not redeclare what .card-art / .card-unavailable already say");
        }
    }

    [Fact]
    public void the_screen_shell_owns_the_layout_of_the_pages_that_use_it()
    {
        // The shell cancels .page's centring (a child cannot undo a parent's max-width) and does
        // the centring once, in .screen-body. Both halves have to be present or the welcome screen
        // renders either double-padded or edge-to-edge.
        var css = File.ReadAllText(Path.Combine(RepoRoot(), "DiffusionNexus.Installer.Electron", "wwwroot", "app.css"));

        var optOut = Regex.Match(css, @"\.page:has\(>\s*\.screen\)\s*\{(?<body>[^}]*)\}").Groups["body"].Value;
        optOut.Should().Contain("max-width: none").And.Contain("padding: 0");

        var body = Regex.Match(css, @"\.screen-body\s*\{(?<body>[^}]*)\}").Groups["body"].Value;
        body.Should().Contain("max-width: 1000px").And.Contain("margin-inline: auto").And.Contain("padding-inline: 24px");

        // And the screens themselves must NOT carry their own copy of it any more.
        foreach (var screen in new[] { "welcome", "workload-screen" })
        {
            var rule = Regex.Match(css, $@"\.{screen}\s*\{{(?<body>[^}}]*)\}}").Groups["body"].Value;
            rule.Should().NotBeNullOrEmpty();
            rule.Should().NotContain("margin-inline: auto", $".{screen} must not re-centre what .screen-body centres");
        }
    }

    [Fact]
    public void app_css_has_balanced_braces_outside_comments_and_strings()
    {
        var path = Path.Combine(RepoRoot(), "DiffusionNexus.Installer.Electron", "wwwroot", "app.css");
        var css = File.ReadAllText(path);
        css = Regex.Replace(css, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        css = Regex.Replace(css, "\"(?:[^\"\\\\]|\\\\.)*\"|'(?:[^'\\\\]|\\\\.)*'", string.Empty);

        var depth = 0;
        var line = 1;
        foreach (var ch in css)
        {
            if (ch == '\n') line++;
            if (ch == '{') depth++;
            if (ch == '}') depth--;
            // No upper bound: @media > @keyframes > frame, @supports inside @media and native
            // nesting all legitimately go deeper than two. The failure this guards (a dropped
            // closing brace) shows up as depth != 0 at the end regardless.
            depth.Should().BeGreaterThanOrEqualTo(0, $"a '}}' without an opener appears around line {line}");
        }

        depth.Should().Be(0, "every block that is opened must be closed");
    }
}
