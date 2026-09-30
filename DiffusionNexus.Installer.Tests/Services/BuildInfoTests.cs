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
    public void The_json_is_one_object_with_the_release_scripts_property_names()
    {
        // New-Release.ps1 reads .app, .sdk and .catalogSchema from this text with ConvertFrom-Json,
        // and the catalog repo's gate reads .catalogSchema. The names are a contract.
        var text = BuildInfo.ToJson();

        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        root.ValueKind.Should().Be(JsonValueKind.Object);
        root.GetProperty("app").GetString().Should().Be(AppVersion.Display);
        root.GetProperty("sdk").GetString().Should().NotContain("+");
        root.GetProperty("catalogSchema").GetInt32().Should().Be(CatalogSchema.Supported);
        root.GetProperty("builtAt").GetDateTimeOffset().Should().Be(BuildInfo.Create().BuiltAt);
        text.Should().NotContain("\"App\"", "property names are camelCase");
    }
}
