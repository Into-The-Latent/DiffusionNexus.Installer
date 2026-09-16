using System.Globalization;
using DiffusionNexus.Installer.Core.Install;
using FluentAssertions;
using Xunit;
using SdkLogLevel = DiffusionNexus.Installer.SDK.Models.Enums.LogLevel;

namespace DiffusionNexus.Installer.Tests.Install;

/// <summary>
/// The one text rendering of the log, shared by the file written at the end of a run and the
/// "Copy log" button, so what a user pastes into an issue is what they would find on disk.
/// </summary>
public class InstallLogFileTests
{
    // Local time, because that is what the formatter prints: a user reading the file beside the
    // clock on their wall must not have to convert from UTC.
    private static InstallLogLine Line(string message, SdkLogLevel level = SdkLogLevel.Info) =>
        new(new DateTimeOffset(new DateTime(2026, 9, 16, 14, 30, 2, DateTimeKind.Local)), message, level);

    [Fact]
    public void Every_line_carries_its_time_and_level()
    {
        var text = InstallLogFile.Format([Line("Cloning", SdkLogLevel.Info), Line("boom", SdkLogLevel.Error)], truncatedLines: 0);

        text.Should().Contain("[14:30:02] [Info] Cloning");
        text.Should().Contain("[14:30:02] [Error] boom");
        text.Should().NotContain("truncated");
    }

    [Fact]
    public void A_truncated_buffer_is_announced_before_the_first_line_it_still_holds()
    {
        var text = InstallLogFile.Format([Line("first kept")], truncatedLines: 12);

        text.IndexOf("12 earlier lines truncated", StringComparison.Ordinal)
            .Should().BeLessThan(text.IndexOf("first kept", StringComparison.Ordinal));
    }

    [Fact]
    public void The_file_name_is_timestamped_like_the_1x_wizard_wrote_it()
    {
        InstallLogFile.FileName(new DateTimeOffset(2026, 9, 16, 14, 30, 2, TimeSpan.Zero))
            .Should().Be("installation-log-verbose-2026-09-16-14-30-02.txt");
    }

    [Fact]
    public void Dates_are_gregorian_whatever_the_machine_culture_is()
    {
        // Review finding: culture-formatted yyyy is the era year under a Buddhist (th-TH) or
        // Umm al-Qura (ar-SA) default calendar, so the file would be named 2569-... and support
        // telling a user to look for the 2026 file would find nothing.
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("th-TH");
            var when = new DateTimeOffset(new DateTime(2026, 9, 16, 14, 30, 2, DateTimeKind.Local));

            InstallLogFile.FileName(when).Should().Be("installation-log-verbose-2026-09-16-14-30-02.txt");
            InstallLogFile.Compose("W", @"C:\x", "Completed", when, [Line("hello")], 0)
                .Should().Contain("Generated: 2026-09-16 14:30:02").And.Contain("[14:30:02] [Info] hello");
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void A_file_that_cannot_be_written_yields_null_rather_than_a_throw()
    {
        // TryWrite is called from InstallSession.StartAsync's finally, ahead of the notification
        // that ends the run on screen; anything escaping it leaves the screen on "Installing".
        var folder = Directory.CreateTempSubdirectory("dn-logfile-").FullName;
        try
        {
            var when = new DateTimeOffset(2026, 9, 16, 14, 30, 2, TimeSpan.Zero);
            Directory.CreateDirectory(Path.Combine(folder, InstallLogFile.FileName(when)));   // a directory where the file must go

            var act = () => InstallLogFile.TryWrite(folder, "text", when);

            act.Should().NotThrow().Which.Should().BeNull();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
