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
    public void A_link_whose_name_is_unknown_until_download_keeps_the_model_from_counting_as_present()
    {
        // Review finding: a dotless link (real name arrives with Content-Disposition) yields no
        // target, and "all targets present" must not be answered over the survivors alone -- the
        // row would say "already downloaded" and the estimate would zero the whole model while
        // that link still downloads.
        Touch(@"models\unet\unet.gguf");
        var model = Model("Mixed", @"models\unet",
            Link("https://host.invalid/files/unet.gguf"),
            Link("https://civitai.com/api/download/models/12345"));

        var presence = _scanner.Scan(Request(Workload(model))).Single();

        presence.AllPartsPresent.Should().BeFalse();
        presence.ExistingPath.Should().BeNull();
        presence.UnresolvableLinks.Should().Be(1);
        presence.Targets.Should().ContainSingle().Which.ExistingPath.Should().NotBeNull();
    }

    [Fact]
    public void The_directory_cache_key_keeps_a_drive_root_intact()
    {
        // Review finding: trimming the separator off "D:\" yields "D:", a drive-RELATIVE path that
        // enumerates the process's current directory on D: instead of the root.
        ModelPresenceScanner.NormalizeDirectoryKey(@"D:\").Should().Be(@"D:\");
        ModelPresenceScanner.NormalizeDirectoryKey(@"C:\models\loras\").Should().Be(@"C:\models\loras");
        ModelPresenceScanner.NormalizeDirectoryKey(@"C:\models\loras").Should().Be(@"C:\models\loras");
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

    [Fact]
    public void A_query_string_filename_is_honoured_like_the_downloader_does()
    {
        // Matches FileDownloader.GetFileNameFromUrl: no dotted filename in the path falls back to
        // a "filename=" query parameter.
        var model = Model("Query", @"models\x", Link("https://host.invalid/download?filename=model.safetensors"));

        _scanner.Scan(Request(Workload(model))).Single().Targets.Single().FileName.Should().Be("model.safetensors");
    }

    [Fact]
    public void A_url_without_a_dotted_filename_yields_no_target()
    {
        // A real catalog link (Krea 2 Identity Edit): the path segment is a numeric id, and there is
        // no "filename=" query either -- the real name only arrives with the server's
        // Content-Disposition header at download time, so this must not produce an unmatchable target.
        var model = Model("Civitai", @"models\x", Link("https://civitai.com/api/download/models/3139172?fileId=3019297"));

        var presence = _scanner.Scan(Request(Workload(model))).Single();

        presence.Targets.Should().BeEmpty();
        presence.AllPartsPresent.Should().BeFalse();
    }

    [Fact]
    public void A_filename_with_wildcard_characters_does_not_match_other_files()
    {
        // Directory.GetFiles(dir, pattern, ...) treats '*'/'?' in the pattern as wildcards; matching
        // by plain name equality means a URL-decoded '*' in a filename can never match another file.
        Touch(@"models\x\real.bin");
        var model = Model("Wildcard", @"models\x", Link("https://host.invalid/%2A.bin"));

        _scanner.Scan(Request(Workload(model))).Single().Targets.Single().ExistingPath.Should().BeNull();
    }

    [Fact]
    public void The_scanner_lists_each_destination_directory_once_per_scan()
    {
        // Cannot assert on the number of Directory.GetFiles calls from here (nothing to intercept
        // the real filesystem), so this pins the *behaviour* the per-scan directory cache must
        // preserve: three link-less models sharing one destination, none of the files present,
        // still resolve independently and correctly to three absent targets.
        var models = new[]
        {
            new ModelDownload { Name = "A", Destination = @"models\shared", Url = "https://host.invalid/a.bin" },
            new ModelDownload { Name = "B", Destination = @"models\shared", Url = "https://host.invalid/b.bin" },
            new ModelDownload { Name = "C", Destination = @"models\shared", Url = "https://host.invalid/c.bin" },
        };

        var presences = _scanner.Scan(Request(Workload(models)));

        presences.Should().HaveCount(3);
        presences.Should().OnlyContain(p => !p.AllPartsPresent && p.Targets.Single().ExistingPath == null);
    }

    public void Dispose()
    {
        try { Directory.Delete(_repo, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
