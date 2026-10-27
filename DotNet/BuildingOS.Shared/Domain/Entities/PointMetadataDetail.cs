namespace BuildingOS.Shared;

/// <summary>Read identity and units only; native bounds are not part of this view.</summary>
public sealed record PointReadMetadata(string DtId, string Id, string Name, string? Unit, bool? Writable);

/// <summary>Building-scoped metadata with the existing topology and authoritative control schema.</summary>
public sealed record PointMetadataDetail(
    PointReadMetadata Point, Building? Building, Floor? Floor, Space? Space, Device? Device, ControlSchema? ControlSchema)
{
    public static PointMetadataDetail FromDetail(PointDetail detail) => new(
        new(detail.Point.DtId, detail.Point.Id, detail.Point.Name, detail.Point.Unit, detail.Point.Writable),
        detail.Building, detail.Floor, detail.Space, detail.Device, detail.ControlSchema);
}
