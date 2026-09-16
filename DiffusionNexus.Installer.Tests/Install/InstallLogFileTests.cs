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
}
