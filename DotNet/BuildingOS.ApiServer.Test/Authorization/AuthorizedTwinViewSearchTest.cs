using BuildingOs.ApiServer.Authorization;
using BuildingOS.Shared;
using BuildingOS.Shared.Domain.Authorization;
using BuildingOS.Shared.Infrastructure;
using Moq;

namespace BuildingOS.ApiServer.Test.Authorization;

public class AuthorizedTwinViewSearchTest
{
    private static AuthorizationContext AdminAuth() => new() { UserId = "admin1", Role = "admin", Permissions = [] };
    private static AuthorizationContext UserAuth() => new() { UserId = "user1", Role = "user", Permissions = [] };

    private static ResourceSearchHit Hit(string type, string dtId, string id, string name, string? buildingDtId = null) =>
        new() { Type = type, DtId = dtId, Id = id, Name = name, BuildingDtId = buildingDtId };

    private static (AuthorizedTwinView view, Mock<IAuthorizationService> auth) Build(
        ResourceSearchHit[] hits)
    {
        var db = new Mock<IDigitalTwinDatabase>();
        db.Setup(d => d.SearchResources(
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<int>(), It.IsAny<int>()))
          .ReturnsAsync(hits);

        var authSvc = new Mock<IAuthorizationService>();
        // default: nothing accessible
        authSvc.Setup(s => s.GetAccessibleResourceIdsAsync(
                It.IsAny<AuthorizationContext>(), It.IsAny<string>(), "read", It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)[]);

        return (new AuthorizedTwinView(db.Object, authSvc.Object), authSvc);
    }

    private static void GrantType(Mock<IAuthorizationService> auth, string type, params string[] rawIds)
    {
        var hashed = rawIds.Select(PermissionHelper.HashResourceId).ToArray();
        auth.Setup(s => s.GetAccessibleResourceIdsAsync(
                It.IsAny<AuthorizationContext>(), type, "read", It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)hashed);
    }

    [Fact]
    public async Task Search_Admin_ReturnsAllHits()
    {
        var hits = new[]
        {
            Hit("building", "urn:b1", "B1", "Bldg 1"),
            Hit("point", "urn:pt1", "PT001", "Temp"),
        };
        var (view, _) = Build(hits);

        var result = await view.SearchAsync(AdminAuth(), "x", null, null, [], 50, 0, default);

        Assert.Equal(2, result.Length);
    }

    [Fact]
    public async Task Search_User_ReturnsOnlyAccessibleByOwnId()
    {
        var hits = new[]
        {
            Hit("floor", "urn:f1", "F1", "1F"),
            Hit("floor", "urn:f2", "F2", "2F"),
        };
        var (view, auth) = Build(hits);
        GrantType(auth, "floor", "urn:f1"); // floor matched by DtId

        var result = await view.SearchAsync(UserAuth(), "F", null, null, [], 50, 0, default);

        Assert.Single(result);
        Assert.Equal("urn:f1", result[0].DtId);
    }

    [Fact]
    public async Task Search_User_PointMatchedByBusinessId_NotDtId()
    {
        var hits = new[] { Hit("point", "urn:pt1", "PT001", "Temp") };
        var (view, auth) = Build(hits);
        GrantType(auth, "point", "PT001"); // point matched by business Id, not DtId

        var result = await view.SearchAsync(UserAuth(), "Temp", null, null, [], 50, 0, default);

        Assert.Single(result);
        Assert.Equal("PT001", result[0].Id);
    }

    [Fact]
    public async Task Search_User_PointNotAccessibleByDtId_IsExcluded()
    {
        var hits = new[] { Hit("point", "urn:pt1", "PT001", "Temp") };
        var (view, auth) = Build(hits);
        GrantType(auth, "point", "urn:pt1"); // wrong key (DtId) — should NOT match

        var result = await view.SearchAsync(UserAuth(), "Temp", null, null, [], 50, 0, default);

        Assert.Empty(result);
    }

    [Fact]
    public async Task Search_User_BuildingAncestorGrant_IncludesDescendant()
    {
        // device itself not directly granted, but its building is → visible via ancestor.
        var hits = new[] { Hit("device", "urn:dev1", "DEV1", "AC", buildingDtId: "urn:b1") };
        var (view, auth) = Build(hits);
        GrantType(auth, "building", "urn:b1");

        var result = await view.SearchAsync(UserAuth(), "AC", null, null, [], 50, 0, default);

        Assert.Single(result);
        Assert.Equal("urn:dev1", result[0].DtId);
    }

    [Fact]
    public async Task Search_User_NoGrants_ReturnsEmpty()
    {
        var hits = new[] { Hit("space", "urn:s1", "S1", "Room") };
        var (view, _) = Build(hits);

        var result = await view.SearchAsync(UserAuth(), "Room", null, null, [], 50, 0, default);

        Assert.Empty(result);
    }

    [Fact]
    public async Task Search_ForwardsTagsToDatabase_ThenRbacFilters()
    {
        // Tags are a SPARQL pre-filter; the RBAC pass runs after. Verify the tags reach the DB query
        // and that an admin (no RBAC reduction) still receives the tag-matched hit.
        var db = new Mock<IDigitalTwinDatabase>();
        IReadOnlyList<string>? captured = null;
        db.Setup(d => d.SearchResources(
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<int>(), It.IsAny<int>()))
          .Callback<string?, string?, string?, IReadOnlyList<string>, int, int>((_, _, _, tags, _, _) => captured = tags)
          .ReturnsAsync(new[] { Hit("point", "urn:pt1", "PT001", "Temp") });
        var authSvc = new Mock<IAuthorizationService>();
        var view = new AuthorizedTwinView(db.Object, authSvc.Object);

        var result = await view.SearchAsync(AdminAuth(), null, "point", null, ["hvac", "temperature"], 50, 0, default);

        Assert.NotNull(captured);
        Assert.Equal(new[] { "hvac", "temperature" }, captured!);
        Assert.Single(result);
    }
}
