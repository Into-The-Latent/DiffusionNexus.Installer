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
    public void the_welcome_screen_fills_the_window_it_is_given()
    {
        // This screen is the whole window, and it went wrong in both directions before it was
        // right. First everything wrapped and the community links fell off a 16:9 window; then
        // everything was pinned to the size that fit a 720p one, so a maximised window showed a
        // small screen marooned in the top-left with dead space around it. Each rule below is one
        // of those two failures, and each has an obvious-looking "simplification" that restores it.
        var css = File.ReadAllText(Path.Combine(RepoRoot(), "DiffusionNexus.Installer.Electron", "wwwroot", "app.css"));

        // The banner is a 16:9 image. Spanning the column makes it ~260px tall AND crops the
        // wordmark, so it is capped by width -- and that cap scales, or it is a 480px postage
        // stamp on a 4K display.
        var banner = Regex.Match(css, @"\.welcome-banner\s*\{(?<body>[^}]*)\}").Groups["body"].Value;
        banner.Should().Contain("width: min(100%, clamp(440px, 38vw, 820px))");

        var welcome = Regex.Match(css, @"\.welcome\s*\{(?<body>[^}]*)\}").Groups["body"].Value;
        welcome.Should().Contain("--tile: clamp(190px, 14vw, 264px)", "the tile width is what scales the strip");
        welcome.Should().Contain("flex: 1").And.Contain("justify-content: center",
            "leftover height belongs evenly above and below the content, not all of it underneath");

        // A tile that grows or shrinks resizes its own artwork as the page count changes.
        var card = Regex.Match(css, @"\.software-card\s*\{(?<body>[^}]*)\}").Groups["body"].Value;
        card.Should().Contain("flex: 0 0 var(--tile)");

        var track = Regex.Match(css, @"\.jukebox-track\s*\{(?<body>[^}]*)\}").Groups["body"].Value;
        track.Should().Contain("display: flex").And.Contain("overflow-x: auto");
        track.Should().NotContain("flex-wrap", "a strip that wraps is the grid this replaced");
        track.Should().Contain("justify-content: safe center",
            "plain `center` pushes the first tile past the scroll start, where it cannot be reached");
        track.Should().NotContain("scroll-snap",
            "mandatory snapping re-snapped on its own and opened the strip at its far end");

        Regex.IsMatch(css, @"\.software-grid\s*\{").Should().BeFalse("the wrapping grid is gone, so its rule must go too");

        // The shell has to have height before a screen can fill it.
        var screen = Regex.Match(css, @"\.screen\s*\{(?<body>[^}]*)\}").Groups["body"].Value;
        screen.Should().Contain("min-height: 100vh").And.Contain("flex-direction: column");

        // Every screen's body grows, which is what holds the community footer against the bottom
        // of the window rather than letting it float under short content.
        var body = Regex.Match(css, @"\.screen-body\s*\{(?<body>[^}]*)\}").Groups["body"].Value;
        body.Should().Contain("flex: 1");

        // And the welcome screen is the one page that opts out of the shared 1000px column: at
        // 1000px the six tiles can never all be on screen however large the window gets.
        var wider = Regex.Match(css, @"\.screen-body:has\(>\s*\.welcome\)\s*\{(?<body>[^}]*)\}").Groups["body"].Value;
        wider.Should().Contain("max-width: 1800px");
    }

    [Fact]
    public void the_install_screen_keeps_its_two_columns_inside_the_window()
    {
        // A grid column's default minimum is its CONTENT, so plain 1fr tracks let one unwrapped
        // log line or one long report comment push the whole screen wider than the window -- with
        // no horizontal scrollbar in Electron to get back from it.
        var css = File.ReadAllText(Path.Combine(RepoRoot(), "DiffusionNexus.Installer.Electron", "wwwroot", "app.css"));

        var split = Regex.Match(css, @"\.install-split\s*\{(?<body>[^}]*)\}").Groups["body"].Value;
        split.Should().Contain("grid-template-columns: minmax(0, 1.15fr) minmax(0, 1fr)");

        // And below the app's minimum window width the two columns stack instead of both being
        // too narrow to read.
        var stacked = Regex.Match(css, @"@media \(max-width: 900px\)\s*\{\s*\.install-split\s*\{(?<body>[^}]*)\}");
        stacked.Success.Should().BeTrue("the columns must stack on a narrow window");
        stacked.Groups["body"].Value.Should().Contain("grid-template-columns: minmax(0, 1fr)");
    }

    [Fact]
    public void the_install_screen_fills_the_window_it_is_given()
    {
        // Both halves of "use the space". Without the width opt-out a maximised 1900px window
        // shows two ~460px columns marooned between 900px margins; without the height chain the
        // panels stop at their content and leave the bottom third of the window empty. Each link
        // of that chain needs min-height: 0 -- a flex item's default minimum is its own content,
        // so one missing line silently cancels every shrink below it.
        var css = File.ReadAllText(Path.Combine(RepoRoot(), "DiffusionNexus.Installer.Electron", "wwwroot", "app.css"));

        var body = Regex.Match(css, @"\.screen-body:has\(\.install-split\)\s*\{(?<body>[^}]*)\}").Groups["body"].Value;
        body.Should().Contain("max-width: 1800px").And.Contain("min-height: 0");

        var screen = Regex.Match(css, @"\.screen:has\(\.install-split\)\s*\{(?<body>[^}]*)\}").Groups["body"].Value;
        screen.Should().Contain("height: 100vh",
            "flex items resolve against a definite height; against min-height: 100vh they just take their content's");

        var wizard = Regex.Match(css, @"\.wizard:has\(\.install-split\)\s*\{(?<body>[^}]*)\}").Groups["body"].Value;
        wizard.Should().Contain("flex: 1").And.Contain("min-height: 0");

        var split = Regex.Match(css, @"\.install-split\s*\{(?<body>[^}]*)\}").Groups["body"].Value;
        split.Should().Contain("flex: 1").And.Contain("min-height: 0");

        // The backstop under all of it. A pinned 100vh screen has nowhere to put overflow, so a box
        // that cannot shrink to fit painted straight over the buttons below it -- unscrollable.
        var panel = Regex.Match(css, @"\.install-split > \.panel\s*\{(?<body>[^}]*)\}").Groups["body"].Value;
        panel.Should().Contain("overflow: hidden");

        // And the escape hatch keys on height as well as width: the height is what runs out first
        // at the 650px minimum window Program.cs sets.
        Regex.IsMatch(css, @"@media \(max-width: 900px\), \(max-height: \d+px\)").Should().BeTrue(
            "a window can be too short for the filled layout as easily as too narrow");
    }

    [Fact]
    public void the_install_outcome_keeps_its_colour()
    {
        // These were descendant selectors (.result-ok h3) matching an <h3> inside a wrapper div.
        // The heading became the element carrying the class, which left both rules matching nothing
        // and every outcome -- complete, cancelled and failed -- in the default heading colour.
        // Nothing else would notice: the component tests assert on text, which did not change.
        // Comments stripped first -- the rule above the fix names the old selector, and a search
        // for it would find that sentence and pass.
        var css = Regex.Replace(
            File.ReadAllText(Path.Combine(RepoRoot(), "DiffusionNexus.Installer.Electron", "wwwroot", "app.css")),
            @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);

        foreach (var rule in new[] { "result-ok", "result-bad" })
        {
            Regex.IsMatch(css, $@"\.{rule}\s*\{{").Should().BeTrue(
                $".{rule} must style the element that carries it");
            Regex.IsMatch(css, $@"\.{rule}\s+\w").Should().BeFalse(
                $".{rule} must not be a descendant selector -- nothing is nested inside the heading");
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
