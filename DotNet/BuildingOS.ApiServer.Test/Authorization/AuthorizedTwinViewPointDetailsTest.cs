using BuildingOs.ApiServer.Authorization;
using BuildingOS.Shared;
using BuildingOS.Shared.Domain.Authorization;
using BuildingOS.Shared.Infrastructure;
using Moq;

namespace BuildingOS.ApiServer.Test.Authorization;

/// <summary>
/// データ健全性一覧（#452）が使う建物スコープの Point 台帳読み取りの認可。
///
/// <para>ここの肝は<b>認可の判定を台帳を読む前に済ませる</b>こと。buildingDtId は
/// <c>GET /api/telemetry/health?buildingDtId=…</c> で呼び出し元が自由に指定できるので、先に読んでから
/// 絞る実装だと「1 件も読めない利用者」でも建物全件の SPARQL と認可前キャッシュの充填を誘発できる。
/// データ自体は漏れない（どちらでも空が返る）が、実在しない ID を並べるだけで台帳キャッシュを
/// 太らせられるため、読める見込みがゼロなら台帳に触れないことをテストで固定する。</para>
/// </summary>
public class AuthorizedTwinViewPointDetailsTest
{
    private const string Building = "urn:test:building-1";

    private static AuthorizationContext AdminAuth() => new() { UserId = "admin1", Role = "admin", Permissions = [] };
    private static AuthorizationContext UserAuth() => new() { UserId = "user1", Role = "user", Permissions = [] };

    private static PointDetail Detail(string pointId, string? deviceDtId = null) => new()
    {
        Point = new Point { DtId = $"urn:test:pt:{pointId}", Id = pointId, Name = pointId },
        Device = deviceDtId is null
            ? null
            : new Device { DtId = deviceDtId, Id = deviceDtId, Name = deviceDtId },
    };

    private static (AuthorizedTwinView view, Mock<IDigitalTwinDatabase> db, Mock<IAuthorizationService> auth) Build(
        params PointDetail[] inventory)
    {
        var db = new Mock<IDigitalTwinDatabase>();
        db.Setup(d => d.ListPointDetails(It.IsAny<string>())).ReturnsAsync(inventory);

        var authSvc = new Mock<IAuthorizationService>();
        authSvc.Setup(s => s.CanAccessAsync(
                It.IsAny<AuthorizationContext>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        authSvc.Setup(s => s.GetAccessibleResourceIdsAsync(
                It.IsAny<AuthorizationContext>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());

        return (new AuthorizedTwinView(db.Object, authSvc.Object), db, authSvc);
    }

    private static void GrantIds(Mock<IAuthorizationService> auth, string resourceType, params string[] rawIds)
        => auth.Setup(s => s.GetAccessibleResourceIdsAsync(
                It.IsAny<AuthorizationContext>(), resourceType, "read", It.IsAny<CancellationToken>()))
            .ReturnsAsync(rawIds.Select(PermissionHelper.HashResourceId).ToArray());

    [Fact]
    public async Task Admin_SeesEveryPointInTheBuilding()
    {
        var (view, db, _) = Build(Detail("PT001"), Detail("PT002"));

        var result = await view.ListPointDetailsAsync(AdminAuth(), Building, default);

        Assert.Equal(2, result.Length);
        db.Verify(d => d.ListPointDetails(Building), Times.Once());
    }

    [Fact]
    public async Task BuildingReadGrant_SeesEveryPointInTheBuilding()
    {
        var (view, db, auth) = Build(Detail("PT001"), Detail("PT002"));
        auth.Setup(s => s.CanAccessAsync(
                It.IsAny<AuthorizationContext>(), "building", Building, "read", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await view.ListPointDetailsAsync(UserAuth(), Building, default);

        Assert.Equal(2, result.Length);
        db.Verify(d => d.ListPointDetails(Building), Times.Once());
    }

    /// <summary>建物権限が無くても、直接付与された Point は見える（台帳は読む必要がある）。</summary>
    [Fact]
    public async Task PointGrantOnly_SeesOnlyThatPoint()
    {
        var (view, db, auth) = Build(Detail("PT001"), Detail("PT002"));
        GrantIds(auth, "point", "PT002");

        var result = await view.ListPointDetailsAsync(UserAuth(), Building, default);

        Assert.Equal("PT002", Assert.Single(result).Point.Id);
        db.Verify(d => d.ListPointDetails(Building), Times.Once());
    }

    /// <summary>機器の read 権があれば、その配下の Point は見える（ListPointsAsync と同じ規則）。</summary>
    [Fact]
    public async Task DeviceGrantOnly_SeesPointsUnderThatDevice()
    {
        var (view, _, auth) = Build(Detail("PT001", "urn:test:dev-a"), Detail("PT002", "urn:test:dev-b"));
        GrantIds(auth, "device", "urn:test:dev-b");

        var result = await view.ListPointDetailsAsync(UserAuth(), Building, default);

        Assert.Equal("PT002", Assert.Single(result).Point.Id);
    }

    /// <summary>
    /// 権限が 1 つも無い利用者には空を返し、<b>台帳を読みに行かない</b>。
    /// 認可前の全件ロードとキャッシュ充填を、任意の建物 ID で誘発させないための回帰テスト。
    /// </summary>
    [Fact]
    public async Task NoGrantsAtAll_ReturnsEmptyWithoutTouchingTheTwin()
    {
        var (view, db, _) = Build(Detail("PT001"), Detail("PT002"));

        var result = await view.ListPointDetailsAsync(UserAuth(), Building, default);

        Assert.Empty(result);
        db.Verify(d => d.ListPointDetails(It.IsAny<string>()), Times.Never());
    }

    /// <summary>IRI として壊れた dtId は SPARQL にも認可にも渡さない（#446 のガード）。</summary>
    [Fact]
    public async Task MalformedDtId_ReturnsEmptyWithoutTouchingTheTwin()
    {
        var (view, db, auth) = Build(Detail("PT001"));

        var result = await view.ListPointDetailsAsync(AdminAuth(), "not an iri>", default);

        Assert.Empty(result);
        db.Verify(d => d.ListPointDetails(It.IsAny<string>()), Times.Never());
        auth.Verify(s => s.CanAccessAsync(
                It.IsAny<AuthorizationContext>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never());
    }
}
