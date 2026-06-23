namespace BuildingOS.Shared.Domain.Authorization;

public record AuthorizationContext
{
    public required string UserId { get; init; }
    public required string Role { get; init; }
    public required IReadOnlyList<string> Permissions { get; init; }

    public bool IsAdmin => Role == "admin";
}
