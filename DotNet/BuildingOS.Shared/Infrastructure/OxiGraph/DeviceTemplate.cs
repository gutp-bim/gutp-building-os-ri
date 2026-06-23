namespace BuildingOS.Shared.Infrastructure.OxiGraph;

public record DeviceTemplateProperty(string Name, string Access, string PointType);

public record DeviceTemplate(
    string Namespace,
    string DeviceType,
    string ClassName,
    DeviceTemplateProperty[] Properties);

public record DeviceTemplateValidationError(
    string EquipmentId,
    string DeviceType,
    string[] MissingPointTypes);
