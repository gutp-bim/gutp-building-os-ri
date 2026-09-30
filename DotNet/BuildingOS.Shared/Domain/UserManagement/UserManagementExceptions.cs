namespace BuildingOS.Shared.Domain.UserManagement;

/// <summary>The Keycloak user does not exist (the Admin API answered 404). The controller maps it to 404.</summary>
public sealed class UserNotFoundException(string userId) : Exception($"User {userId} not found")
{
    public string UserId { get; } = userId;
}

/// <summary>
/// Keycloak accepted an attribute write but a re-read shows it did not store it — e.g. a Keycloak 24+
/// realm without <c>unmanagedAttributePolicy: ADMIN_EDIT</c>, which answers the PUT with 204 and drops
/// undeclared attributes. Reporting that as success is how #519 went unnoticed; the controller maps it
/// to 502 with a failure audit.
/// </summary>
public sealed class UserAttributesNotPersistedException(string userId) : Exception(
    $"Keycloak did not store the role/permission attributes for user {userId}. " +
    "Check that the realm's user profile sets unmanagedAttributePolicy to ADMIN_EDIT.")
{
    public string UserId { get; } = userId;
}
