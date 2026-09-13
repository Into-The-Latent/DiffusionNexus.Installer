using DiffusionNexus.Installer.Electron.Endpoints;
using FluentAssertions;
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
}
