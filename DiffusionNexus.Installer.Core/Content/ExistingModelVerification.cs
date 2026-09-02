using DiffusionNexus.Installer.SDK.Services.Installation.Utilities;

namespace DiffusionNexus.Installer.Core.Content;

/// <summary>Seam over the SDK's sealed ExistingModelVerifier so the pre-flight can be tested without network.</summary>
public interface IExistingModelVerifier
{
    Task<IReadOnlyList<ExistingModelMismatch>> VerifyAsync(
        IReadOnlyList<ExistingModelCandidate> candidates,
        CancellationToken ct = default);
}

public sealed class SdkExistingModelVerifier(UrlSizeResolver sizeResolver) : IExistingModelVerifier
{
    private readonly ExistingModelVerifier _verifier = new(sizeResolver);

    public Task<IReadOnlyList<ExistingModelMismatch>> VerifyAsync(
        IReadOnlyList<ExistingModelCandidate> candidates,
        CancellationToken ct = default) => _verifier.VerifyAsync(candidates, ct);
}
