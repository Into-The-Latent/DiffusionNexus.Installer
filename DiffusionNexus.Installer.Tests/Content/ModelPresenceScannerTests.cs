// DiffusionNexus.Installer.Tests/Content/ModelPresenceScannerTests.cs
using DiffusionNexus.Installer.Core.Content;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Entities;
using DiffusionNexus.Installer.SDK.Models.Enums;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Content;

/// <summary>
/// One scan replaces 1.x's two hand-synced copies (CheckExistingModels for display and
/// BuildExistingModelCandidates for pre-flight). Filesystem cases use a temp folder as the
/// repository path and a relative model destination under it.
/// </summary>
public sealed class ModelPresenceScannerTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), $"dn-scan-{Guid.NewGuid():N}");
    private readonly ModelPresenceScanner _scanner = new();

    private static readonly IReadOnlyDictionary<string, string> NoOverrides =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public ModelPresenceScannerTests() => Directory.CreateDirectory(_repo);

    private static InstallationConfiguration Workload(params ModelDownload[] models)
    {
        var w = new InstallationConfiguration();
        w.Repository.Type = RepositoryType.ComfyUI;
        w.ModelDownloads.AddRange(models);
        return w;
    }

    private static ModelDownload Model(string name, string destination, params ModelDownloadLink[] links)
    {
        var m = new ModelDownload { Name = name, Destination = destination };
        m.DownloadLinks.AddRange(links);
        return m;
    }

    private static ModelDownloadLink Link(string url, VramProfile? vram = null) =>
        new() { Url = url, VramProfile = vram };

    private ModelScanRequest Request(InstallationConfiguration workload, int tier = 0) =>
        new(workload, _repo, null, NoOverrides, tier);

    private string Touch(string relative)
    {
        var path = Path.Combine(_repo, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    [Fact]
    public void A_file_in_the_destination_marks_the_model_present()
    {
        var expected = Touch(@"models\vae\ae.safetensors");
        var model = Model("VAE", @"models\vae", Link("https://host.invalid/files/ae.safetensors"));

        var presence = _scanner.Scan(Request(Workload(model))).Single();

        presence.AllPartsPresent.Should().BeTrue();
        presence.ExistingPath.Should().Be(expected);
        presence.Targets.Single().FileName.Should().Be("ae.safetensors");
        presence.Targets.Single().DestinationDirectory.Should().Be(Path.Combine(_repo, @"models\vae"));
    }

    [Fact]
    public void A_file_filed_into_a_subfolder_still_counts()
    {
        // Users sort models into subfolders ("Wan 2.2\..."); 1.x searched recursively and so do we.
        Touch(@"models\unet\Wan 2.2\wan.gguf");
        var model = Model("Wan", @"models\unet", Link("https://host.invalid/wan.gguf"));

        _scanner.Scan(Request(Workload(model))).Single().AllPartsPresent.Should().BeTrue();
    }

    [Fact]
    public void A_multi_part_model_with_one_part_missing_is_not_present()
    {
        Touch(@"models\clip\part1.safetensors");
        var model = Model("CLIP", @"models\clip",
            Link("https://host.invalid/part1.safetensors"),
            Link("https://host.invalid/part2.safetensors"));

        var presence = _scanner.Scan(Request(Workload(model))).Single();

        presence.AllPartsPresent.Should().BeFalse();
        presence.ExistingPath.Should().BeNull();
        presence.Targets.Should().HaveCount(2);
        presence.Targets.Count(t => t.ExistingPath is not null).Should().Be(1, "the pre-flight verifies the part that exists");
    }

    [Fact]
    public void Only_the_links_the_pipeline_would_download_at_the_tier_are_targets()
    {
        var model = Model("Tiered", @"models\unet",
            Link("https://host.invalid/q8.gguf", VramProfile.VRAM_8GB),
            Link("https://host.invalid/q16.gguf", VramProfile.VRAM_16GB));

        var atEight = _scanner.Scan(Request(Workload(model), tier: 8)).Single();
        var unfiltered = _scanner.Scan(Request(Workload(model), tier: 0)).Single();

        atEight.Targets.Select(t => t.FileName).Should().Equal("q8.gguf");
        unfiltered.Targets.Select(t => t.FileName).Should().Equal("q8.gguf", "q16.gguf");
    }

    [Fact]
    public void A_link_less_model_falls_back_to_its_url_and_honours_the_model_level_tier()
    {
        var model = new ModelDownload { Name = "Direct", Destination = @"models\x", Url = "https://host.invalid/big.safetensors", VramProfile = VramProfile.VRAM_16GB };

        _scanner.Scan(Request(Workload(model), tier: 8)).Single().Targets.Should().BeEmpty("16 GB does not fit 8 GB");
        _scanner.Scan(Request(Workload(model), tier: 0)).Single().Targets.Single().FileName.Should().Be("big.safetensors");
    }

    [Fact]
    public void A_model_with_neither_links_nor_url_has_no_targets_and_is_not_present()
    {
        var model = new ModelDownload { Name = "Empty", Destination = @"models\x" };

        var presence = _scanner.Scan(Request(Workload(model))).Single();

        presence.Targets.Should().BeEmpty();
        presence.AllPartsPresent.Should().BeFalse();
    }

    [Fact]
    public void A_link_destination_placeholder_resolves_under_the_repository_like_the_pipeline()
    {
        var link = Link("https://host.invalid/lora.safetensors");
        link.Destination = @"{RepositoryPath}\models\loras";
        var model = Model("LoRA", @"models\x", link);

        _scanner.Scan(Request(Workload(model))).Single().Targets.Single().DestinationDirectory
            .Should().Be(Path.Combine(_repo, @"models\loras"));
    }

    [Fact]
    public void Disabled_models_and_disabled_links_are_ignored()
    {
        var disabledModel = Model("Off", @"models\x", Link("https://host.invalid/a.bin"));
        disabledModel.Enabled = false;
        var disabledLink = Link("https://host.invalid/b.bin");
        disabledLink.Enabled = false;
        var model = Model("On", @"models\x", disabledLink, Link("https://host.invalid/c.bin"));

        var results = _scanner.Scan(Request(Workload(disabledModel, model)));

        results.Should().ContainSingle().Which.Targets.Select(t => t.FileName).Should().Equal("c.bin");
    }

    [Fact]
    public void A_destination_that_is_a_file_rather_than_a_folder_counts_as_absent_without_throwing()
    {
        Touch(@"models\notafolder");
        var model = Model("Odd", @"models\notafolder", Link("https://host.invalid/m.bin"));

        var act = () => _scanner.Scan(Request(Workload(model)));

        act.Should().NotThrow();
        act().Single().AllPartsPresent.Should().BeFalse();
    }

    [Fact]
    public void A_url_with_no_file_name_yields_no_target()
    {
        var model = Model("Bare", @"models\x", Link("https://host.invalid/"));

        _scanner.Scan(Request(Workload(model))).Single().Targets.Should().BeEmpty();
    }

    public void Dispose()
    {
        try { Directory.Delete(_repo, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
