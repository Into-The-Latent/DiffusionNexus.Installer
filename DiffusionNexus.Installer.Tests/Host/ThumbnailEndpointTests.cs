using DiffusionNexus.Installer.Core.Catalog;
using DiffusionNexus.Installer.Electron.Endpoints;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Moq;
using Xunit;

namespace DiffusionNexus.Installer.Tests.Host;

/// <summary>
/// Content-type derivation for the thumbnail endpoint. The catalog writer emits
/// "thumbnail.&lt;ext&gt;" preserving the source extension, so webp is the common case but not the
/// only one -- serving a PNG as image/webp makes it fail to decode in Chromium.
/// </summary>
public class ThumbnailEndpointTests
{
    [Theory]
    [InlineData(@"C:\catalog\workloads\krea\thumbnail.webp", "image/webp")]
    [InlineData(@"C:\catalog\workloads\krea\thumbnail.png", "image/png")]
    [InlineData(@"C:\catalog\workloads\krea\thumbnail.jpg", "image/jpeg")]
    [InlineData(@"C:\catalog\workloads\krea\thumbnail.jpeg", "image/jpeg")]
    [InlineData(@"C:\catalog\workloads\krea\THUMBNAIL.WEBP", "image/webp")]
    public void Derives_the_content_type_from_the_extension(string path, string expected)
    {
        ThumbnailEndpoint.ContentTypeFor(path).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"C:\catalog\workloads\krea\thumbnail.tiff")]
    [InlineData(@"C:\catalog\workloads\krea\thumbnail")]
    public void Falls_back_to_octet_stream_for_anything_it_does_not_know(string? path)
    {
        // Never guess image/webp for an unknown extension: a wrong content type renders as a
        // broken image, which reads as "the catalog is broken" rather than "we shipped an odd file".
        ThumbnailEndpoint.ContentTypeFor(path).Should().Be("application/octet-stream");
    }

    private static InstallationConfiguration InstallerWorkload(Guid id, string? thumbnailPath) =>
        new() { Id = id, ThumbnailPath = thumbnailPath };

    [Fact]
    public async Task Unknown_id_returns_404_without_reading_any_thumbnail_bytes()
    {
        var workloadId = Guid.NewGuid();
        var workloads = new Mock<IWorkloadSource>(MockBehavior.Strict);
        workloads
            .Setup(w => w.GetInstallerWorkloadsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<InstallationConfiguration>)[]);

        var result = await ThumbnailEndpoint.HandleAsync(
            workloadId, workloads.Object, new DefaultHttpContext().Response, CancellationToken.None);

        result.Should().BeOfType<NotFound>();

        // MockBehavior.Strict means an unexpected GetThumbnailAsync call throws instead of
        // returning a default -- so if the handler ever reads bytes before checking the
        // installer-filtered list, this test fails on that call rather than merely on the status
        // code.
        workloads.Verify(w => w.GetThumbnailAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Workload_outside_the_installer_list_returns_404_even_though_bytes_exist()
    {
        // This is the DiffusionNexusCore regression: GetThumbnailAsync has no WorkloadTarget
        // filter and would happily return bytes for a workload this installer never offers.
        // The mock is set up so it WOULD return bytes if called, so this test only passes because
        // the authorization gate stops it from being called at all -- not because there was
        // nothing to find.
        var workloadId = Guid.NewGuid();
        var workloads = new Mock<IWorkloadSource>(MockBehavior.Strict);
        workloads
            .Setup(w => w.GetInstallerWorkloadsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<InstallationConfiguration>)[]);
        workloads
            .Setup(w => w.GetThumbnailAsync(workloadId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([1, 2, 3]);

        var result = await ThumbnailEndpoint.HandleAsync(
            workloadId, workloads.Object, new DefaultHttpContext().Response, CancellationToken.None);

        result.Should().BeOfType<NotFound>();
        workloads.Verify(w => w.GetThumbnailAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Installer_workload_with_no_thumbnail_returns_404()
    {
        var workloadId = Guid.NewGuid();
        var workload = InstallerWorkload(workloadId, @"C:\catalog\workloads\krea\thumbnail.webp");
        var workloads = new Mock<IWorkloadSource>(MockBehavior.Strict);
        workloads
            .Setup(w => w.GetInstallerWorkloadsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<InstallationConfiguration>)[workload]);
        workloads
            .Setup(w => w.GetThumbnailAsync(workloadId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((byte[]?)null);

        var result = await ThumbnailEndpoint.HandleAsync(
            workloadId, workloads.Object, new DefaultHttpContext().Response, CancellationToken.None);

        result.Should().BeOfType<NotFound>();
    }

    [Fact]
    public async Task Installer_workload_with_bytes_returns_a_file_result_with_the_derived_content_type_and_cache_header()
    {
        var workloadId = Guid.NewGuid();
        var workload = InstallerWorkload(workloadId, @"C:\catalog\workloads\krea\thumbnail.png");
        var bytes = new byte[] { 1, 2, 3, 4 };
        var workloads = new Mock<IWorkloadSource>(MockBehavior.Strict);
        workloads
            .Setup(w => w.GetInstallerWorkloadsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<InstallationConfiguration>)[workload]);
        workloads
            .Setup(w => w.GetThumbnailAsync(workloadId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(bytes);

        var response = new DefaultHttpContext().Response;

        var result = await ThumbnailEndpoint.HandleAsync(
            workloadId, workloads.Object, response, CancellationToken.None);

        var file = result.Should().BeOfType<FileContentHttpResult>().Subject;
        file.ContentType.Should().Be("image/png");
        file.FileContents.ToArray().Should().Equal(bytes);
        response.Headers.CacheControl.ToString().Should().Be("private, max-age=3600");
    }
}
