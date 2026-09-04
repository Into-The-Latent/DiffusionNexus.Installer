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
