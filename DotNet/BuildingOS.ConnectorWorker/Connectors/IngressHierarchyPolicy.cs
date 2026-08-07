namespace BuildingOS.ConnectorWorker.Connectors;

/// <summary>Outcome of the ingress hierarchy-completeness check (#292).</summary>
public enum IngressHierarchyDecision
{
    /// <summary>Accept: enforcement off, or the point resolves to both a building and a device.</summary>
    Allow,

    /// <summary>Reject: enforcement on but the twin places the point under no building.</summary>
    RejectNoBuildingPath,

    /// <summary>Reject: enforcement on but the twin links the point to no device.</summary>
    RejectNoDeviceLink,
}

/// <summary>
/// Pure decision for whether a point's twin metadata places it in the building hierarchy (#292).
/// Every field of <see cref="BuildingOS.Shared.Module.PointMetadata"/> is optional ("empty =
/// undefined"), so a point that was seeded without a building/device reaches the ingress with them
/// blank. Kept side-effect-free so it is exhaustively unit-tested; the service layer maps the
/// decision to a skip + metric.
/// </summary>
public static class IngressHierarchyPolicy
{
    /// <param name="enforce">Whether hierarchy completeness is required (false ⇒ legacy accept-all).</param>
    /// <param name="building">The point's building from the twin (empty when undefined).</param>
    /// <param name="deviceId">The point's device from the twin (empty when undefined).</param>
    public static IngressHierarchyDecision Check(bool enforce, string? building, string? deviceId)
    {
        if (!enforce) return IngressHierarchyDecision.Allow;
        // Building first: it is the coarser break, so a point missing both is reported by the
        // outermost link that failed (matching how the twin-import preview classifies orphans, #291).
        if (string.IsNullOrWhiteSpace(building)) return IngressHierarchyDecision.RejectNoBuildingPath;
        return string.IsNullOrWhiteSpace(deviceId)
            ? IngressHierarchyDecision.RejectNoDeviceLink
            : IngressHierarchyDecision.Allow;
    }
}
