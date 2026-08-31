using DiffusionNexus.Installer.Core.DevTools;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.DevTools;

/// <summary>
/// The export exists to make script-generation bugs visible in a click instead of a full install.
/// These check the part that gives it its value: that it reports what actually reached disk.
/// </summary>
public sealed class LauncherScriptPreviewTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"dn-export-{Guid.NewGuid():N}");

    private static InstallationConfiguration Workload(RepositoryType type, string name = "Test Workload")
    {
        var w = new InstallationConfiguration { Name = name };
        w.Repository.Type = type;
        w.Torch.TorchVersion = "2.8.0";
        w.Torch.CudaVersion = "12.8";
        return w;
    }

    [Theory]
    [InlineData(RepositoryType.A1111)]
    [InlineData(RepositoryType.Forge)]
    [InlineData(RepositoryType.Fooocus)]
    [InlineData(RepositoryType.ComfyUI)]
    [InlineData(RepositoryType.AIToolkit)]
    [InlineData(RepositoryType.AceStep)]
    public void Every_workload_type_exports_something(RepositoryType type)
    {
        var written = new LauncherScriptPreview().Export(Workload(type), _dir);

        written.Should().NotBeEmpty($"{type} must produce at least a launcher");
        foreach (var s in written)
            File.Exists(Path.Combine(_dir, "Test Workload", s.FileName)).Should().BeTrue();
    }

    [Theory]
    [InlineData(RepositoryType.A1111)]
    [InlineData(RepositoryType.Forge)]
    [InlineData(RepositoryType.Fooocus)]
    [InlineData(RepositoryType.ComfyUI)]
    public void The_reported_line_endings_match_what_is_actually_on_disk(RepositoryType type)
    {
        // Deliberately NOT "the scripts are correct". Whether the SDK emits CRLF is the SDK's
        // business and is pinned by its own suite; asserting it here would make this project's CI
        // red for every SDK version that has not yet shipped the fix — and a tool whose tests only
        // pass against one SDK build is not a tool you can trust to tell you about a build.
        //
        // What MUST hold regardless of SDK version is that the verdict is honest: a reporter that
        // says "CRLF OK" about an LF file is worse than no reporter, because it would have hidden
        // exactly the bug this was built for.
        var written = new LauncherScriptPreview().Export(Workload(type), _dir);

        foreach (var script in written)
        {
            var bytes = File.ReadAllBytes(Path.Combine(_dir, "Test Workload", script.FileName));
            var crlf = 0;
            var bareLf = 0;

            for (var i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] != (byte)'\n') continue;
                if (i > 0 && bytes[i - 1] == (byte)'\r') crlf++; else bareLf++;
            }

            var truth = (crlf, bareLf) switch
            {
                (0, 0) => "none",
                ( > 0, 0) => "CRLF",
                (0, > 0) => "LF",
                _ => "mixed",
            };

            script.LineEnding.Should().Be(truth, $"{script.FileName} must be reported as it is on disk");
            script.Bytes.Should().Be(bytes.Length);
        }
    }

    [Fact]
    public void A_batch_file_with_bare_LF_is_reported_as_wrong()
    {
        // Guards the verdict itself: if IsCorrect were inverted or always true, every test above
        // would pass vacuously and the tool would cheerfully greenlight the shipped bug.
        new ExportedScript("w", "run_nvidia.bat", 10, "LF").IsCorrect.Should().BeFalse();
        new ExportedScript("w", "run_nvidia.bat", 10, "CRLF").IsCorrect.Should().BeTrue();
        new ExportedScript("w", "start.sh", 10, "CRLF").IsCorrect.Should().BeFalse();
        new ExportedScript("w", "start.sh", 10, "LF").IsCorrect.Should().BeTrue();
    }

    [Fact]
    public void Each_workload_gets_its_own_folder()
    {
        // Every ComfyUI pack writes run_nvidia.bat. A flat dump would silently leave only the last.
        var preview = new LauncherScriptPreview();

        preview.Export(Workload(RepositoryType.ComfyUI, "Pack A"), _dir);
        preview.Export(Workload(RepositoryType.ComfyUI, "Pack B"), _dir);

        File.Exists(Path.Combine(_dir, "Pack A", "run_nvidia.bat")).Should().BeTrue();
        File.Exists(Path.Combine(_dir, "Pack B", "run_nvidia.bat")).Should().BeTrue();
    }

    [Fact]
    public void A_workload_name_that_is_not_a_legal_folder_name_still_exports()
    {
        // Real catalog names contain '&' and '/' -- "FlashVSR-Video&Image Upscale".
        var written = new LauncherScriptPreview()
            .Export(Workload(RepositoryType.ComfyUI, "Bad/Name:With*Chars"), _dir);

        written.Should().NotBeEmpty();
    }

    [Fact]
    public void The_branding_header_is_in_the_exported_batch_file()
    {
        // What the tool is actually looked at for: the logo that was mis-rendering.
        new LauncherScriptPreview().Export(Workload(RepositoryType.A1111), _dir);

        File.ReadAllText(Path.Combine(_dir, "Test Workload", "run_nvidia.bat"))
            .Should().Contain("Formerly known as AIKnowledge2Go");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
