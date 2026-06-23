using BuildingOS.Shared.Domain.Authorization;
using Microsoft.Extensions.Logging;

namespace BuildingOS.Shared.Test.Domain.Authorization;

public class DefaultAuthorizationServiceTest
{
    private readonly Mock<IGroupMembershipResolver> _groupResolverMock;
    private readonly Mock<IResourceHierarchyResolver> _hierarchyResolverMock;
    private readonly DefaultAuthorizationService _sut;

    public DefaultAuthorizationServiceTest()
    {
        _groupResolverMock = new Mock<IGroupMembershipResolver>();
        _hierarchyResolverMock = new Mock<IResourceHierarchyResolver>();
        var loggerMock = new Mock<ILogger<DefaultAuthorizationService>>();

        // デフォルト: 祖先なし、グループなし
        _hierarchyResolverMock
            .Setup(x => x.GetAncestorsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<(string, string)>());
        _groupResolverMock
            .Setup(x => x.GetGroupsContainingResourceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());
        _groupResolverMock
            .Setup(x => x.GetGroupMembersAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());

        _sut = new DefaultAuthorizationService(
            _groupResolverMock.Object,
            _hierarchyResolverMock.Object,
            loggerMock.Object);
    }

    // テスト用ヘルパー: 生IDからハッシュ化済みパーミッション文字列を生成
    private static string P(string type, string id, string actions) =>
        PermissionHelper.BuildPermissionString(type, id, actions);

    private static string H(string id) => PermissionHelper.HashResourceId(id);

    // === CanAccessAsync ===

    [Fact]
    public async Task CanAccessAsync_Admin_ReturnsTrue()
    {
        var context = CreateContext("admin", Array.Empty<string>());

        var result = await _sut.CanAccessAsync(context, "device", "ahu-301", "read");

        Assert.True(result);
    }

    [Fact]
    public async Task CanAccessAsync_DirectPermission_ReturnsTrue()
    {
        var context = CreateContext("user", new[] { P("device", "ahu-301", "read") });

        var result = await _sut.CanAccessAsync(context, "device", "ahu-301", "read");

        Assert.True(result);
    }

    [Fact]
    public async Task CanAccessAsync_ActionMismatch_ReturnsFalse()
    {
        var context = CreateContext("user", new[] { P("device", "ahu-301", "read") });

        var result = await _sut.CanAccessAsync(context, "device", "ahu-301", "write");

        Assert.False(result);
    }

    [Fact]
    public async Task CanAccessAsync_ResourceMismatch_ReturnsFalse()
    {
        var context = CreateContext("user", new[] { P("device", "ahu-301", "read") });

        var result = await _sut.CanAccessAsync(context, "device", "ahu-999", "read");

        Assert.False(result);
    }

    [Fact]
    public async Task CanAccessAsync_AdminAction_GrantsAllActions()
    {
        var context = CreateContext("user", new[] { P("device", "ahu-301", "admin") });

        Assert.True(await _sut.CanAccessAsync(context, "device", "ahu-301", "read"));
        Assert.True(await _sut.CanAccessAsync(context, "device", "ahu-301", "write"));
    }

    [Fact]
    public async Task CanAccessAsync_CommaSeparatedActions_Matches()
    {
        var context = CreateContext("user", new[] { P("device", "ahu-301", "read,write") });

        Assert.True(await _sut.CanAccessAsync(context, "device", "ahu-301", "read"));
        Assert.True(await _sut.CanAccessAsync(context, "device", "ahu-301", "write"));
    }

    [Fact]
    public async Task CanAccessAsync_WritePermission_GrantsRead()
    {
        var context = CreateContext("user", new[] { P("device", "ahu-301", "write") });

        Assert.True(await _sut.CanAccessAsync(context, "device", "ahu-301", "read"));
        Assert.True(await _sut.CanAccessAsync(context, "device", "ahu-301", "write"));
    }

    [Fact]
    public async Task GetAccessibleResourceIdsAsync_WritePermission_GrantsRead()
    {
        var context = CreateContext("user", new[] { P("device", "ahu-301", "write") });

        var result = await _sut.GetAccessibleResourceIdsAsync(context, "device", "read");

        Assert.Contains(H("ahu-301"), result);
    }

    [Fact]
    public async Task CanAccessAsync_GroupPermission_ReturnsTrue()
    {
        var context = CreateContext("user", new[] { P("group", "hvac-team", "read") });

        _groupResolverMock
            .Setup(x => x.GetGroupsContainingResourceAsync("device", "ahu-301", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "hvac-team" });

        var result = await _sut.CanAccessAsync(context, "device", "ahu-301", "read");

        Assert.True(result);
    }

    [Fact]
    public async Task CanAccessAsync_GroupPermission_WrongGroup_ReturnsFalse()
    {
        var context = CreateContext("user", new[] { P("group", "power-team", "read") });

        _groupResolverMock
            .Setup(x => x.GetGroupsContainingResourceAsync("device", "ahu-301", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "hvac-team" });

        var result = await _sut.CanAccessAsync(context, "device", "ahu-301", "read");

        Assert.False(result);
    }

    [Fact]
    public async Task CanAccessAsync_HierarchyPermission_BuildingGrantsDevice()
    {
        var context = CreateContext("user", new[] { P("building", "eng2", "read") });

        _hierarchyResolverMock
            .Setup(x => x.GetAncestorsAsync("device", "ahu-301", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new (string, string)[]
            {
                ("space", "room-301"),
                ("floor", "eng2-3f"),
                ("building", "eng2")
            });

        var result = await _sut.CanAccessAsync(context, "device", "ahu-301", "read");

        Assert.True(result);
    }

    [Fact]
    public async Task CanAccessAsync_HierarchyPermission_FloorGrantsDevice()
    {
        var context = CreateContext("user", new[] { P("floor", "eng2-3f", "read") });

        _hierarchyResolverMock
            .Setup(x => x.GetAncestorsAsync("device", "ahu-301", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new (string, string)[]
            {
                ("space", "room-301"),
                ("floor", "eng2-3f"),
                ("building", "eng2")
            });

        var result = await _sut.CanAccessAsync(context, "device", "ahu-301", "read");

        Assert.True(result);
    }

    [Fact]
    public async Task CanAccessAsync_HierarchyAndGroupPermission_ReturnsTrue()
    {
        // group権限→buildingがグループ内→deviceにアクセス
        var context = CreateContext("user", new[] { P("group", "campus-group", "read") });

        _hierarchyResolverMock
            .Setup(x => x.GetAncestorsAsync("device", "ahu-301", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new (string, string)[]
            {
                ("space", "room-301"),
                ("floor", "eng2-3f"),
                ("building", "eng2")
            });

        // deviceのグループ → 空
        _groupResolverMock
            .Setup(x => x.GetGroupsContainingResourceAsync("device", "ahu-301", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());
        // spaceのグループ → 空
        _groupResolverMock
            .Setup(x => x.GetGroupsContainingResourceAsync("space", "room-301", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());
        // floorのグループ → 空
        _groupResolverMock
            .Setup(x => x.GetGroupsContainingResourceAsync("floor", "eng2-3f", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());
        // buildingのグループ → campus-group
        _groupResolverMock
            .Setup(x => x.GetGroupsContainingResourceAsync("building", "eng2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "campus-group" });

        var result = await _sut.CanAccessAsync(context, "device", "ahu-301", "read");

        Assert.True(result);
    }

    [Fact]
    public async Task CanAccessAsync_InvalidPermissionFormat_Skipped()
    {
        var context = CreateContext("user", new[] { "invalid-format", "only:two", P("device", "ahu-301", "read") });

        var result = await _sut.CanAccessAsync(context, "device", "ahu-301", "read");

        Assert.True(result);
    }

    [Fact]
    public async Task CanAccessAsync_NoPermissions_ReturnsFalse()
    {
        var context = CreateContext("user", Array.Empty<string>());

        var result = await _sut.CanAccessAsync(context, "device", "ahu-301", "read");

        Assert.False(result);
    }

    // === GetAccessibleResourceIdsAsync ===

    [Fact]
    public async Task GetAccessibleResourceIdsAsync_Admin_ReturnsEmpty()
    {
        var context = CreateContext("admin", Array.Empty<string>());

        var result = await _sut.GetAccessibleResourceIdsAsync(context, "device", "read");

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetAccessibleResourceIdsAsync_DirectPermission_ReturnsHashedIds()
    {
        var context = CreateContext("user", new[]
        {
            P("device", "ahu-301", "read"),
            P("device", "ahu-302", "read,write"),
            P("device", "ahu-303", "write"),
            P("building", "eng2", "read")
        });

        var result = await _sut.GetAccessibleResourceIdsAsync(context, "device", "read");

        Assert.Contains(H("ahu-301"), result);
        Assert.Contains(H("ahu-302"), result);
        Assert.Contains(H("ahu-303"), result); // write権限があればreadも許可
        Assert.DoesNotContain(H("eng2"), result);
    }

    [Fact]
    public async Task GetAccessibleResourceIdsAsync_GroupPermission_ExpandsMembers()
    {
        var context = CreateContext("user", new[] { P("group", "hvac-team", "read") });

        _groupResolverMock
            .Setup(x => x.GetGroupMembersAsync("hvac-team", "device", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "ahu-301", "ahu-302" });

        var result = await _sut.GetAccessibleResourceIdsAsync(context, "device", "read");

        // グループメンバーもハッシュ化されて返される
        Assert.Contains(H("ahu-301"), result);
        Assert.Contains(H("ahu-302"), result);
    }

    [Fact]
    public async Task GetAccessibleResourceIdsAsync_AdminAction_GrantsAll()
    {
        var context = CreateContext("user", new[] { P("device", "ahu-301", "admin") });

        var result = await _sut.GetAccessibleResourceIdsAsync(context, "device", "read");

        Assert.Contains(H("ahu-301"), result);
    }

    [Fact]
    public async Task GetAccessibleResourceIdsAsync_WithAncestorPermission_DoesNotExpandToDescendants()
    {
        // building:hash(eng2):read → device一覧は空（逆引きはListで非対応）
        var context = CreateContext("user", new[] { P("building", "eng2", "read") });

        var result = await _sut.GetAccessibleResourceIdsAsync(context, "device", "read");

        Assert.Empty(result);
    }

    private static AuthorizationContext CreateContext(string role, IReadOnlyList<string> permissions) =>
        new()
        {
            UserId = "test-user",
            Role = role,
            Permissions = permissions
        };
}
