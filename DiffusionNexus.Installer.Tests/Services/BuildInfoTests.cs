using System.Reflection;
using System.Text.Json;
using DiffusionNexus.Installer.Electron.Services;
using DiffusionNexus.Installer.SDK.Catalog;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Services;

public class BuildInfoTests
{
    [Fact]
    public void Handles_only_the_exact_flag()
    {
        // Electron starts the .NET side with its own arguments; only the release script passes
        // --build-info, and it must be matched as a plain argument, never as a prefix or a fragment.
        BuildInfo.Handles(["--build-info"]).Should().BeTrue();
        BuildInfo.Handles(["/electronPort=1234", "--build-info"]).Should().BeTrue();
        BuildInfo.Handles([]).Should().BeFalse();
        BuildInfo.Handles(["/electronPort=1234"]).Should().BeFalse();
        BuildInfo.Handles(["--build-info=1"]).Should().BeFalse();
        BuildInfo.Handles(["--build-infos"]).Should().BeFalse();
    }

    [Fact]
    public void The_document_names_this_build()
    {
        var info = BuildInfo.Create();

        info.App.Should().Be(AppVersion.Display);
        info.Sdk.Should().MatchRegex(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?$", "the informational version minus any +sha");
        info.Sdk.Should().Be(AppVersion.Strip(typeof(CatalogSchema).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion));
        info.CatalogSchema.Should().Be(CatalogSchema.Supported);
        info.BuiltAt.Should().BeAfter(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero))
            .And.BeBefore(DateTimeOffset.UtcNow.AddMinutes(1));
    }

    [Fact]
    public void Without_an_assembly_file_builtAt_is_null_not_now()
    {
        // A single-file publish or an assembly loaded from bytes has no Location. The stamp is then absent,
        // not the moment --build-info happened to run: promotion reads this asset as fact.
        BuildInfo.Create(assemblyLocation: "").BuiltAt.Should().BeNull();
        BuildInfo.Create(Path.Combine(Path.GetTempPath(), "does-not-exist.dll")).BuiltAt.Should().BeNull();
        BuildInfo.Create(typeof(BuildInfo).Assembly.Location).BuiltAt.Should().NotBeNull();
    }

    [Fact]
    public void The_document_names_the_embedded_catalog_seed()
    {
        // Step 1c compares this with the seed Step 0d judged, and Promote-Release passes it to
        // Test-CatalogSeed -Expect. It is the embedded manifest.json, read the way the app reads it.
        using var stream = typeof(BuildInfo).Assembly.GetManifestResourceStream("manifest.json")!;
        using var doc = JsonDocument.Parse(stream);
        var embedded = doc.RootElement;

        var seed = BuildInfo.Create().CatalogSeed;

        seed.Version.Should().Be(embedded.GetProperty("catalogVersion").GetInt32());
        seed.Commit.Should().Be(embedded.GetProperty("commit").GetString()).And.MatchRegex("^[0-9a-f]{40}$");
        seed.Sha256.Should().Be(embedded.GetProperty("archive").GetProperty("sha256").GetString()).And.MatchRegex("^[0-9a-f]{64}$");
        // What the SDK records from the seed besides those three: Test-CatalogSeed -Expect needs them
        // to refuse a Preview stamp or a hand-made pack time over the right archive.
        seed.Channel.Should().Be(embedded.GetProperty("channel").GetString());
        seed.GeneratedAt.Should().Be(embedded.GetProperty("generatedAt").GetDateTimeOffset());
    }

    [Fact]
    public void Without_a_complete_embedded_manifest_there_is_no_answer()
    {
        // A build with no seed, or a seed manifest without commit or archive, must not print a
        // document that promotion would trust: the exception ends the process with a non-zero exit,
        // and Step 1c refuses the build.
        var location = typeof(BuildInfo).Assembly.Location;
        var noSeed = () => BuildInfo.Create(location, () => null, EmbeddedArchive);
        var noCommit = () => BuildInfo.Create(location, () => new MemoryStream("{\"catalogVersion\":5,\"archive\":{\"name\":\"catalog.zip\",\"sha256\":\"ab\",\"bytes\":1}}"u8.ToArray()), EmbeddedArchive);
        var noArchive = () => BuildInfo.Create(location, () => new MemoryStream("{\"catalogVersion\":5,\"commit\":\"abc\"}"u8.ToArray()), EmbeddedArchive);

        noSeed.Should().Throw<InvalidOperationException>().WithMessage("*manifest.json*missing*");
        noCommit.Should().Throw<InvalidOperationException>().WithMessage("*commit*");
        noArchive.Should().Throw<InvalidOperationException>().WithMessage("*archive*");
    }

    [Fact]
    public void An_embedded_archive_that_is_not_the_manifests_is_no_answer()
    {
        // The sha256 in the answer is a fact about the bytes this binary carries, not the manifest's
        // claim: promotion trusts it. A missing or different catalog.zip resource gets no document.
        var location = typeof(BuildInfo).Assembly.Location;
        var noZip = () => BuildInfo.Create(location, EmbeddedManifest, () => null);
        var otherZip = () => BuildInfo.Create(location, EmbeddedManifest, () => new MemoryStream("not the archive"u8.ToArray()));

        noZip.Should().Throw<InvalidOperationException>().WithMessage("*catalog.zip*missing*");
        otherZip.Should().Throw<InvalidOperationException>().WithMessage("*catalog.zip*sha256*manifest says*");
        BuildInfo.Create(location, EmbeddedManifest, EmbeddedArchive).CatalogSeed.Sha256.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void A_refused_answer_is_a_message_on_stderr_and_exit_1_never_a_crash()
    {
        // An unhandled exception would abort the process with a stack trace, a crash dump and, on some
        // machines, a "stopped working" dialog that leaves the release script waiting.
        var output = new StringWriter();
        var error = new StringWriter();

        var exit = BuildInfo.Run(output, error, () => throw new InvalidOperationException("The embedded catalog.zip has sha256 x; its manifest says y."));

        exit.Should().Be(1);
        output.ToString().Should().BeEmpty("a refused build prints no document");
        error.ToString().Should().Contain("its manifest says y").And.NotContain(" at ", "a message, not a stack trace");
    }

    [Fact]
    public void An_answer_is_the_json_on_stdout_and_exit_0()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exit = BuildInfo.Run(output, error, BuildInfo.Create);

        exit.Should().Be(0);
        error.ToString().Should().BeEmpty();
        using var doc = JsonDocument.Parse(output.ToString());
        doc.RootElement.GetProperty("app").GetString().Should().Be(AppVersion.Display);
    }

    private static Stream? EmbeddedManifest() => typeof(BuildInfo).Assembly.GetManifestResourceStream("manifest.json");

    private static Stream? EmbeddedArchive() => typeof(BuildInfo).Assembly.GetManifestResourceStream("catalog.zip");

    [Fact]
    public void The_json_is_one_object_with_the_release_scripts_property_names()
    {
        // New-Release.ps1 reads .app, .sdk and .catalogSchema from this text with ConvertFrom-Json,
        // and the catalog repo's gate reads .catalogSchema. The names are a contract.
        var text = BuildInfo.ToJson();
        var expected = BuildInfo.Create();   // once: every Create hashes the embedded archive

        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        root.ValueKind.Should().Be(JsonValueKind.Object);
        root.GetProperty("app").GetString().Should().Be(AppVersion.Display);
        root.GetProperty("sdk").GetString().Should().NotContain("+");
        root.GetProperty("catalogSchema").GetInt32().Should().Be(CatalogSchema.Supported);
        var seed = root.GetProperty("catalogSeed");
        seed.GetProperty("version").GetInt32().Should().Be(expected.CatalogSeed.Version);
        seed.GetProperty("commit").GetString().Should().Be(expected.CatalogSeed.Commit);
        seed.GetProperty("sha256").GetString().Should().Be(expected.CatalogSeed.Sha256);
        seed.GetProperty("channel").GetString().Should().Be("Stable", "the channel is written as its name, never its enum number");
        seed.GetProperty("generatedAt").GetDateTimeOffset().Should().Be(expected.CatalogSeed.GeneratedAt);
        root.GetProperty("builtAt").GetDateTimeOffset().Should().Be(expected.BuiltAt);
        text.Should().NotContain("\"App\"", "property names are camelCase");
    }
}
