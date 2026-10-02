using BuildingOs.ApiServer.Authorization;
using BuildingOS.Shared;
using BuildingOS.Shared.Domain.Authorization;
using BuildingOS.Shared.Infrastructure;
using Moq;

namespace BuildingOS.ApiServer.Test.Authorization;

public class AuthorizedTwinViewTagsTest
{
    private static AuthorizationContext AdminAuth() => new() { UserId = "admin1", Role = "admin", Permissions = [] };
    private static AuthorizationContext UserAuth() => new() { UserId = "user1", Role = "user", Permissions = [] };
    private static AuthorizationContext GroupManager() => new() { UserId = "gm", Role = "group-manager", Permissions = [] };

    private static ResourceTagUsage Use(string type, string dtId, string id, string tag) =>
        new() { Type = type, DtId = dtId, Id = id, Tag = tag };

    private static (AuthorizedTwinView view, Mock<IDigitalTwinDatabase> db, Mock<IAuthorizationService> authSvc) Build(
        params ResourceTagUsage[] usage)
    {
        var db = new Mock<IDigitalTwinDatabase>();
        db.Setup(d => d.ListTagUsage(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(usage);
        var authSvc = new Mock<IAuthorizationService>();
        authSvc.Setup(s => s.GetAccessibleResourceIdsAsync(
                It.IsAny<AuthorizationContext>(), It.IsAny<string>(), "read", It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)[]);
        return (new AuthorizedTwinView(db.Object, authSvc.Object), db, authSvc);
    }

    [Fact]
    public async Task ListTags_Admin_CountsDistinctResourcesPerTag_MostUsedFirst()
    {
        var (view, _, _) = Build(
            Use("point", "urn:p1", "P1", "temperature"), Use("point", "urn:p2", "P2", "temperature"),
            Use("device", "urn:d1", "D1", "critical"),
            Use("point", "urn:p1", "P1", "temperature")); // duplicate row (join fan-out) must not double count

        var tags = await view.ListTagsAsync(AdminAuth(), "", 20, default);

        Assert.Equal([("temperature", 2), ("critical", 1)], tags.Select(t => (t.Tag, t.Count)));
    }

    [Fact]
    public async Task ListTags_User_CountsOnlyReadableResources()
    {
        var (view, _, authSvc) = Build(
            Use("point", "urn:p1", "P1", "secret"), Use("point", "urn:p2", "P2", "secret"),
            Use("point", "urn:p2", "P2", "visible"));
        var auth = UserAuth();
        authSvc.Setup(s => s.GetAccessibleResourceIdsAsync(auth, "point", "read", It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)[PermissionHelper.HashResourceId("P2")]);

        var tags = await view.ListTagsAsync(auth, "", 20, default);

        // P1 is unreadable, so "secret" is held by P2 only.
        Assert.Equal([("secret", 1), ("visible", 1)], tags.Select(t => (t.Tag, t.Count)).OrderBy(t => t.Tag));
    }

    [Fact]
    public async Task ListTags_User_TagOnlyUnreadableResourcesHold_IsNotRevealed()
    {
        var (view, _, _) = Build(Use("point", "urn:p1", "P1", "hidden-tag"));

        var tags = await view.ListTagsAsync(UserAuth(), "", 20, default);

        Assert.Empty(tags);
    }

    [Fact]
    public async Task ListTags_GroupManager_GetsNothing_AndTheTwinIsNotAsked()
    {
        var (view, db, _) = Build(Use("point", "urn:p1", "P1", "x"));

        var tags = await view.ListTagsAsync(GroupManager(), "", 20, default);

        Assert.Empty(tags);
        db.Verify(d => d.ListTagUsage(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ListTags_AppliesLimit()
    {
        var (view, _, _) = Build(
            Use("point", "urn:p1", "P1", "a"), Use("point", "urn:p1", "P1", "b"), Use("point", "urn:p1", "P1", "c"));

        var tags = await view.ListTagsAsync(AdminAuth(), "", 2, default);

        Assert.Equal(2, tags.Length);
    }
}
