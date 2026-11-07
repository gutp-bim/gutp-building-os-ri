namespace BuildingOS.Shared.Infrastructure.Telemetry;

/// <summary>
/// An <see cref="IHotTelemetryStore"/> that can attribute a deferred put to the producer it came
/// from (<c>connector</c> | <c>gateway-grpc</c>), so a failure surfacing later — after the caller
/// has moved on — is still counted under the right <c>source</c> tag in
/// <c>building_os.telemetry.hot_write_failures</c>. <see cref="ValidatedTelemetryHotStore"/> uses
/// this overload when the store offers it and the plain put otherwise.
/// </summary>
public interface ISourceTaggedHotTelemetryStore : IHotTelemetryStore
{
    Task PutAsync(string pointId, ValidTelemetryData data, string source, CancellationToken cancellationToken = default);
}
