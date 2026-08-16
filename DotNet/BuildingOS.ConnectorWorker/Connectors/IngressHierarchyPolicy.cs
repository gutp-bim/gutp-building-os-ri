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
/// Kept side-effect-free so it is exhaustively unit-tested; the service layer maps the decision to a
/// skip + metric.
/// <para>
/// This deliberately takes <c>hasBuildingPath</c> — real graph reachability — and not the
/// denormalized <c>sbco:building</c> literal. Gating on the literal made strict ingress disagree
/// with the import-time orphan check inside the same feature: a point reachable only through the
/// direct-Level or <c>sbco:floor</c> join (both valid without a Room) carries no such literal and
/// would have been rejected, while a
/// point carrying a stale literal for a building that does not exist would have been accepted.
/// The literal is a string nobody joins; it cannot answer this question.
/// </para>
/// </summary>
public static class IngressHierarchyPolicy
{
    /// <param name="enforce">Whether hierarchy completeness is required (false ⇒ legacy accept-all).</param>
    /// <param name="hasBuildingPath">
    /// Whether the twin actually places the point under a Building, by the #291 definition
    /// (Room spatial chain OR direct-Level chain OR sbco:floor literal join, traversed from the
    /// owning equipment).
    /// </param>
    /// <param name="deviceId">The point's device from the twin (empty when undefined).</param>
    public static IngressHierarchyDecision Check(bool enforce, bool hasBuildingPath, string? deviceId)
    {
        if (!enforce) return IngressHierarchyDecision.Allow;
        // Building first: it is the coarser break, so a point missing both is reported by the
        // outermost link that failed (matching how the twin-import preview classifies orphans, #291).
        if (!hasBuildingPath) return IngressHierarchyDecision.RejectNoBuildingPath;
        return string.IsNullOrWhiteSpace(deviceId)
            ? IngressHierarchyDecision.RejectNoDeviceLink
            : IngressHierarchyDecision.Allow;
    }
}
