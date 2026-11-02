namespace BuildingOS.Shared.Domain.GatewayProvisioning;

/// <summary>Static configuration only. Run identities and current simulation time never belong here.</summary>
public sealed record GatewayProvisioningSnapshot(
    GatewayPointEntry[] Points, GatewayClockSource[] ClockSources, EquipmentClockBinding[] ClockBindings);

public sealed record GatewayClockSource(
    string ClockId, string ClockDomain, string Unit, bool ReadOnly, string AcquisitionMode, BacnetClockNative Native);

public sealed record BacnetClockNative(
    string GatewayId, string ConnectorId, int DeviceInstance, string ObjectType, int ObjectInstance,
    string Property, string Unit)
{
    public string Protocol => "bacnet";
}

public sealed record EquipmentClockBinding(string EquipmentId, string ClockId);

/// <summary>Reads the point list and its associated static clock configuration from one consistent twin view.</summary>
public interface IGatewayProvisioningSource
{
    Task<GatewayProvisioningSnapshot> ReadAsync(string gatewayId, CancellationToken ct);
}

public sealed record TwinClockIssue(string ResourceId, string Reason);

public sealed class ClockConfigurationException(IReadOnlyList<TwinClockIssue> issues)
    : InvalidOperationException("Invalid ClockSource configuration; inspect the import preview for details.")
{
    public IReadOnlyList<TwinClockIssue> Issues { get; } = issues;
}

public sealed class TwinImportConcurrencyException()
    : InvalidOperationException("The twin changed during validation. Preview and retry the import.");
