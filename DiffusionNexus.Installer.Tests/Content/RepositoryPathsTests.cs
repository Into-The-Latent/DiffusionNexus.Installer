// DiffusionNexus.Installer.Tests/Content/RepositoryPathsTests.cs
using DiffusionNexus.Installer.Core.Content;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Content;

public class RepositoryPathsTests
{
    private static InstallationConfiguration Workload(string url)
    {
        var w = new InstallationConfiguration();
        w.Repository.Type = RepositoryType.ComfyUI;
        w.Repository.RepositoryUrl = url;
        return w;
    }

    [Fact]
    public void The_repository_lands_in_a_folder_named_after_the_repo_under_the_install_folder()
        => RepositoryPaths.Resolve(Workload("https://github.com/comfyanonymous/ComfyUI.git"), @"C:\AI")
            .Should().Be(@"C:\AI\ComfyUI");

    [Fact]
    public void An_install_folder_already_named_after_the_repo_is_not_nested_twice()
    {
        // InstallationOrchestrator normalizes this way before the pipeline runs; a scan that did
        // not would look in C:\AI\ComfyUI\ComfyUI and mark every model as missing.
        RepositoryPaths.Resolve(Workload("https://github.com/comfyanonymous/ComfyUI"), @"C:\AI\ComfyUI")
            .Should().Be(@"C:\AI\ComfyUI");
    }

    [Fact]
    public void A_workload_without_a_repository_url_uses_the_install_folder_itself()
        => RepositoryPaths.Resolve(Workload(""), @"C:\AI").Should().Be(@"C:\AI");
}
