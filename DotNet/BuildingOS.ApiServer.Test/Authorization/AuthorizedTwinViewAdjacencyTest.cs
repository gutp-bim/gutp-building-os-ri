using BuildingOs.ApiServer.Authorization;
using BuildingOS.Shared;
using BuildingOS.Shared.Domain.Authorization;
using BuildingOS.Shared.Infrastructure;
using Moq;

namespace BuildingOS.ApiServer.Test.Authorization;

/// <summary>
/// Read authorization for the room-adjacency view (#440). Two gates, not one: the subject room must
/// be readable at all (otherwise the endpoint leaks "this room exists and has N neighbours"), and
/// each neighbour is then filtered on its own read permission.
///
/// The per-neighbour check delegates to <c>IAuthorizationService.CanAccessAsync</c> rather than
/// hash-matching against <c>GetAccessibleResourceIdsAsync("space")</c>: CanAccessAsync already
/// resolves the ancestor chain (building/floor grants) and group permissions, so re-implementing a
/// subset of it here would make a neighbour readable through <c>GET /spaces/{id}</c> yet invisible
/// in <c>GET /spaces/{other}/adjacent-spaces</c>. Adjacency degree is a handful of rooms, so the
/// per-neighbour call costs nothing worth optimizing away.
/// </summary>
public class AuthorizedTwinViewAdjacencyTest
{
    private const string Subject = "urn:test:room-a";

    private static AuthorizationContext AdminAuth() => new() { UserId = "admin1", Role = "admin", Permissions = [] };
    private static AuthorizationContext UserAuth() => new() { UserId = "user1", Role = "user", Permissions = [] };

    private static Space Room(string dtId, string id, string name) => new() { DtId = dtId, Id = id, Name = name };

    private static (AuthorizedTwinView view, Mock<IDigitalTwinDatabase> db, Mock<IAuthorizationService> auth) Build(
        Space[] neighbours, bool subjectExists = true)
    {
        var db = new Mock<IDigitalTwinDatabase>();
        db.Setup(d => d.GetSpace(It.IsAny<string>()))
          .ReturnsAsync(subjectExists ? Room(Subject, "ROOM-A", "Room A") : null);
        db.Setup(d => d.ListAdjacentSpaces(It.IsAny<string>())).ReturnsAsync(neighbours);

        var authSvc = new Mock<IAuthorizationService>();
        // default: nothing accessible
        authSvc.Setup(s => s.CanAccessAsync(
                It.IsAny<AuthorizationContext>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        return (new AuthorizedTwinView(db.Object, authSvc.Object), db, authSvc);
    }

    private static void Grant(Mock<IAuthorizationService> auth, string dtId)
        => auth.Setup(s => s.CanAccessAsync(
                It.IsAny<AuthorizationContext>(), "space", dtId, "read", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

    [Fact]
    public async Task ListAdjacentSpaces_Admin_ReturnsEveryNeighbour()
    {
        var (view, _, _) = Build([Room("urn:test:room-b", "ROOM-B", "Room B"), Room("urn:test:room-c", "ROOM-C", "Room C")]);

        var result = await view.ListAdjacentSpacesAsync(AdminAuth(), Subject, default);

        var ok = Assert.IsType<TwinGetResult<Space[]>.Ok>(result);
        Assert.Equal(2, ok.Resource.Length);
    }

    [Fact]
    public async Task ListAdjacentSpaces_User_WithoutSubjectRead_IsForbidden_AndNeverQueriesAdjacency()
    {
        var (view, db, _) = Build([Room("urn:test:room-b", "ROOM-B", "Room B")]);

        var result = await view.ListAdjacentSpacesAsync(UserAuth(), Subject, default);

        Assert.IsType<TwinGetResult<Space[]>.Forbidden>(result);
        db.Verify(d => d.ListAdjacentSpaces(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ListAdjacentSpaces_UnknownRoom_IsNotFound()
    {
        // An unknown room and a room with no neighbours both produce an empty adjacency list, so the
        // subject's existence is checked separately rather than inferred from the result.
        var (view, _, _) = Build([], subjectExists: false);

        Assert.IsType<TwinGetResult<Space[]>.NotFound>(
            await view.ListAdjacentSpacesAsync(AdminAuth(), Subject, default));
    }

    [Fact]
    public async Task ListAdjacentSpaces_User_ReturnsOnlyReadableNeighbours()
    {
        var (view, _, auth) = Build([
            Room("urn:test:room-b", "ROOM-B", "Room B"),
            Room("urn:test:room-c", "ROOM-C", "Room C"),
        ]);
        Grant(auth, Subject);
        Grant(auth, "urn:test:room-b");

        var result = await view.ListAdjacentSpacesAsync(UserAuth(), Subject, default);

        var ok = Assert.IsType<TwinGetResult<Space[]>.Ok>(result);
        Assert.Equal("urn:test:room-b", Assert.Single(ok.Resource).DtId);
    }

    [Fact]
    public async Task ListAdjacentSpaces_User_HonoursAncestorAndGroupGrants_ViaCanAccess()
    {
        // The neighbour carries no direct space permission — CanAccessAsync says yes because of a
        // building grant / group membership it resolved internally. Filtering on the raw
        // GetAccessibleResourceIdsAsync("space") id list would drop it, and this is the assertion
        // that pins the difference.
        var (view, _, auth) = Build([Room("urn:test:room-b", "ROOM-B", "Room B")]);
        Grant(auth, Subject);
        Grant(auth, "urn:test:room-b");
        auth.Setup(s => s.GetAccessibleResourceIdsAsync(
                It.IsAny<AuthorizationContext>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)[]);

        var result = await view.ListAdjacentSpacesAsync(UserAuth(), Subject, default);

        var ok = Assert.IsType<TwinGetResult<Space[]>.Ok>(result);
        Assert.Single(ok.Resource);
        auth.Verify(s => s.GetAccessibleResourceIdsAsync(
            It.IsAny<AuthorizationContext>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ListAdjacentSpaces_Admin_SkipsPerNeighbourAuthorizationCalls()
    {
        var (view, _, auth) = Build([Room("urn:test:room-b", "ROOM-B", "Room B")]);

        await view.ListAdjacentSpacesAsync(AdminAuth(), Subject, default);

        auth.Verify(s => s.CanAccessAsync(
            It.IsAny<AuthorizationContext>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }
}
