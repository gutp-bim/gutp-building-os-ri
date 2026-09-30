namespace BuildingOS.Shared.Domain.UserManagement;

/// <summary>The Keycloak user does not exist (the Admin API answered 404). The controller maps it to 404.</summary>
public sealed class UserNotFoundException(string userId) : Exception($"User {userId} not found")
{
    public string UserId { get; } = userId;
}

/// <summary>
/// Keycloak accepted an attribute write but a re-read shows none of it — e.g. a Keycloak 24+ realm without
/// <c>unmanagedAttributePolicy: ADMIN_EDIT</c>, which answers the PUT with 204 and drops undeclared
/// attributes (<see cref="KeycloakUserAttributes.LooksPersisted"/>). Reporting that as success is how #519
/// went unnoticed; the controller maps it to 502 with a failure audit and <see cref="StoredAttributes"/>.
/// </summary>
public sealed class UserAttributesNotPersistedException(
    string userId,
    IReadOnlyDictionary<string, string[]>? storedAttributes = null) : Exception(
    $"Keycloak did not store the role/permission attributes for user {userId}. " +
    "Check that the realm's user profile sets unmanagedAttributePolicy to ADMIN_EDIT.")
{
    public string UserId { get; } = userId;

    /// <summary>The role / permission attribute values (new and legacy) Keycloak returned on the re-read.</summary>
    public IReadOnlyDictionary<string, string[]> StoredAttributes { get; } =
        storedAttributes ?? new Dictionary<string, string[]>();
}

/// <summary>
/// Keycloak accepted an attribute write (the PUT answered 2xx) but what followed — the verifying re-read —
/// failed (5xx, timeout, cancellation), so it is unknown whether Keycloak stored it (#532). The write is
/// most likely committed: the controller answers it as a success (so the caller does not retry something
/// already done), saves the reverse-lookup mapping, and marks the audit <c>verified: false</c>.
/// </summary>
public sealed class UserAttributesWrittenUnverifiedException(string userId, EntraUser written, Exception inner)
    : Exception($"Keycloak accepted the attribute update for user {userId} but it could not be verified: {inner.Message}", inner)
{
    public string UserId { get; } = userId;

    /// <summary>
    /// The user as written: the representation read before the PUT with the written role / permission
    /// attributes applied. <see cref="EntraUser.Role"/> is the own role written (groups were not read).
    /// </summary>
    public EntraUser Written { get; } = written;
}
