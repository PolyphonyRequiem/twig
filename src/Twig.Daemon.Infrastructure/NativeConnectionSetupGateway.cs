using Twig.Core;
using Twig.Infrastructure.Services;

namespace Bench.Infrastructure;

public interface INativeConnectionSetupGateway
{
    Task<Result<NativeAadEnrollment[]>> ListAadIdentitiesAsync(CancellationToken ct)
        => Task.FromResult(Result<NativeAadEnrollment[]>.Fail(ErrorCode.Unsupported, "Saved AAD identities are unavailable."));
    Task<Result<NativeAdoDiscoveryPage>> DiscoverAdoAsync(NativeAdoDiscoveryRequest request, CancellationToken ct)
        => Task.FromResult(Result<NativeAdoDiscoveryPage>.Fail(ErrorCode.Unsupported, "ADO setup discovery is unavailable."));
    Task<Result<NativeAadEnrollment>> EnrollAadAsync(NativeAadEnrollmentRequest request,
        Action<Uri> authorizationUrl, CancellationToken ct);
    Task<Result<NativeAdoBinding>> CreateAdoBindingAsync(NativeAdoBindingRequest request, CancellationToken ct);
    Task<Result<NativeSourceObservation>> InitializeFreshAsync(NativeFreshSetupRequest request, CancellationToken ct);
    Task<Result<NativeManagedSetupResult>> InitializeManagedAsync(NativeSetupTarget target, CancellationToken ct)
        => Task.FromResult(Result<NativeManagedSetupResult>.Fail(ErrorCode.Unsupported, "Managed connection setup is unavailable."));
}

public sealed class NativeConnectionSetupGateway(NativeConnectionSetup setup) : INativeConnectionSetupGateway
{
    public Task<Result<NativeAadEnrollment[]>> ListAadIdentitiesAsync(CancellationToken ct)
        => setup.ListAadIdentitiesAsync(ct);
    public Task<Result<NativeAdoDiscoveryPage>> DiscoverAdoAsync(NativeAdoDiscoveryRequest request, CancellationToken ct)
        => setup.DiscoverAdoAsync(request, ct);
    public Task<Result<NativeAadEnrollment>> EnrollAadAsync(NativeAadEnrollmentRequest request,
        Action<Uri> authorizationUrl, CancellationToken ct) => setup.EnrollAadAsync(request, authorizationUrl, ct);
    public Task<Result<NativeAdoBinding>> CreateAdoBindingAsync(NativeAdoBindingRequest request, CancellationToken ct)
        => setup.CreateAdoBindingAsync(request, ct);
    public Task<Result<NativeSourceObservation>> InitializeFreshAsync(NativeFreshSetupRequest request, CancellationToken ct)
        => setup.InitializeFreshAsync(request, ct);
    public Task<Result<NativeManagedSetupResult>> InitializeManagedAsync(NativeSetupTarget target, CancellationToken ct)
        => setup.InitializeManagedAsync(target, ct);
}
