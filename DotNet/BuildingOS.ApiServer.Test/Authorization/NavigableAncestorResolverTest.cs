using BuildingOs.ApiServer.Authorization;
using BuildingOS.Shared.Domain.Authorization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BuildingOS.ApiServer.Test.Authorization;

/// <summary>
/// #548: which nodes a non-admin may see as navigation containers — the ancestors of every floor,
/// space, device and point they can read, resolved by business id (#504).
/// </summary>
public class NavigableAncestorResolverTest
{
    private static AuthorizationContext UserAuth() => new() { UserId = "user1", Role = "user", Permissions = [] };
    private static AuthorizationContext AdminAuth() => new() { UserId = "admin1", Role = "admin", Permissions = [] };

    private sealed record Setup(
        NavigableAncestorResolver Resolver,
        Mock<IAuthorizationService> Auth,
        Mock<IResourceIdMappingRepository> Mapping,
        Mock<IResourceHierarchyResolver> Hierarchy);

    private static Setup Build(IMemoryCache? cache = null)
    {
        var auth = new Mock<IAuthorizationService>();
        auth.Setup(a => a.GetAccessibleResourcesAsync(
                It.IsAny<AuthorizationContext>(), It.IsAny<string>(), "read", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AccessibleResource>());
        var mapping = new Mock<IResourceIdMappingRepository>();
        mapping.Setup(m => m.ResolveOriginalIdsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string>());
        var hierarchy = new Mock<IResourceHierarchyResolver>();
        hierarchy.Setup(h => h.GetAncestorUnionAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<(string, string)>());
        return new Setup(NewResolver(auth, mapping, hierarchy, cache ?? new MemoryCache(new MemoryCacheOptions())),
            auth, mapping, hierarchy);
    }

    /// <summary>The resolver resolves its dependencies from a DI scope of its own, as in production.</summary>
    private static NavigableAncestorResolver NewResolver(
        Mock<IAuthorizationService> auth, Mock<IResourceIdMappingRepository> mapping,
        Mock<IResourceHierarchyResolver> hierarchy, IMemoryCache cache)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => auth.Object);
        services.AddScoped(_ => mapping.Object);
        services.AddScoped(_ => hierarchy.Object);
        return new NavigableAncestorResolver(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), cache);
    }

    private static void Grant(Setup s, string type, params AccessibleResource[] resources)
        => s.Auth.Setup(a => a.GetAccessibleResourcesAsync(
                It.IsAny<AuthorizationContext>(), type, "read", It.IsAny<CancellationToken>()))
            .ReturnsAsync(resources);

    private static AccessibleResource Known(string id) => new(PermissionHelper.HashResourceId(id), id);
    private static AccessibleResource HashOnly(string id) => new(PermissionHelper.HashResourceId(id), null);

    [Fact]
    public async Task ReturnsTheAncestorsOfGrantedSpaces()
    {
        var s = Build();
        Grant(s, "space", Known("R501"));
        s.Hierarchy.Setup(h => h.GetAncestorUnionAsync(
                "space", It.Is<IReadOnlyCollection<string>>(ids => ids.SequenceEqual(new[] { "R501" })),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([("building", "B1"), ("floor", "F5")]);

        var result = await s.Resolver.ResolveAsync(UserAuth(), default);

        Assert.Equal(new[] { ("building", "B1"), ("floor", "F5") }, result.OrderBy(a => a));
    }

    /// <summary>A direct grant stores only a hash; its business id comes from the id-mapping table.</summary>
    [Fact]
    public async Task HashOnlyGrants_AreResolvedThroughTheMappingTable()
    {
        var s = Build();
        var hash = PermissionHelper.HashResourceId("P-1");
        Grant(s, "point", HashOnly("P-1"));
        s.Mapping.Setup(m => m.ResolveOriginalIdsAsync(
                It.Is<IEnumerable<string>>(h => h.Contains(hash)), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string> { [hash] = "P-1" });
        s.Hierarchy.Setup(h => h.GetAncestorUnionAsync(
                "point", It.Is<IReadOnlyCollection<string>>(ids => ids.Contains("P-1")), It.IsAny<CancellationToken>()))
            .ReturnsAsync([("building", "B1"), ("device", "DEV-A")]);

        var result = await s.Resolver.ResolveAsync(UserAuth(), default);

        Assert.Contains(("device", "DEV-A"), result);
    }

    /// <summary>A hash with no mapping cannot be placed in the twin; it is skipped, not guessed.</summary>
    [Fact]
    public async Task UnresolvableHashes_AreSkipped()
    {
        var s = Build();
        Grant(s, "floor", HashOnly("F9"));

        var result = await s.Resolver.ResolveAsync(UserAuth(), default);

        Assert.Empty(result);
        s.Hierarchy.Verify(h => h.GetAncestorUnionAsync(
            It.IsAny<string>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public async Task Admin_AsksNothing()
    {
        var s = Build();

        Assert.Empty(await s.Resolver.ResolveAsync(AdminAuth(), default));
        s.Auth.Verify(a => a.GetAccessibleResourcesAsync(
            It.IsAny<AuthorizationContext>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never());
    }

    /// <summary>Building grants have no ancestors worth listing; only the four lower types are walked.</summary>
    [Fact]
    public async Task WalksFloorSpaceDeviceAndPoint_NotBuilding()
    {
        var s = Build();
        Grant(s, "floor", Known("F5"));
        Grant(s, "space", Known("R501"));
        Grant(s, "device", Known("DEV-A"));
        Grant(s, "point", Known("P-1"));

        await s.Resolver.ResolveAsync(UserAuth(), default);

        foreach (var type in new[] { "floor", "space", "device", "point" })
            s.Hierarchy.Verify(h => h.GetAncestorUnionAsync(
                type, It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()), Times.Once());
        s.Auth.Verify(a => a.GetAccessibleResourcesAsync(
            It.IsAny<AuthorizationContext>(), "building", It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never());
    }

    // ── Cross-request cache (Codex review on #553) ────────────────────────────────────────────
    //
    // /home asks for the devices of every visible space, one HTTP request each, and every request gets
    // a fresh scoped view. Without a shared cache each of those re-resolved the user's whole grant set.

    private static AuthorizationContext User(string id, params string[] permissions)
        => new() { UserId = id, Role = "user", Permissions = permissions };

    [Fact]
    public async Task TheSameUserAndPermissions_AreResolvedOnceAcrossRequests()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var first = Build(cache);
        Grant(first, "space", Known("R501"));
        var second = NewResolver(first.Auth, first.Mapping, first.Hierarchy, cache);

        await first.Resolver.ResolveAsync(User("u1", "sp:abc:r"), default);
        await second.ResolveAsync(User("u1", "sp:abc:r"), default);

        first.Hierarchy.Verify(h => h.GetAncestorUnionAsync(
            "space", It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task DifferentPermissions_AreNotShared()
    {
        var s = Build();
        Grant(s, "space", Known("R501"));

        await s.Resolver.ResolveAsync(User("u1", "sp:abc:r"), default);
        await s.Resolver.ResolveAsync(User("u1", "sp:abc:r", "fl:def:r"), default);
        await s.Resolver.ResolveAsync(User("u2", "sp:abc:r"), default);

        s.Hierarchy.Verify(h => h.GetAncestorUnionAsync(
            "space", It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [Fact]
    public async Task PermissionOrder_DoesNotSplitTheCache()
    {
        var s = Build();
        Grant(s, "space", Known("R501"));

        await s.Resolver.ResolveAsync(User("u1", "a", "b"), default);
        await s.Resolver.ResolveAsync(User("u1", "b", "a"), default);

        s.Hierarchy.Verify(h => h.GetAncestorUnionAsync(
            "space", It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()), Times.Once());
    }

    /// <summary>A failed resolution (OxiGraph down) is not cached; the next request tries again.</summary>
    [Fact]
    public async Task AFailure_IsNotCached()
    {
        var s = Build();
        Grant(s, "space", Known("R501"));
        s.Hierarchy.SetupSequence(h => h.GetAncestorUnionAsync(
                "space", It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("oxigraph down"))
            .ReturnsAsync([("building", "B1")]);

        await Assert.ThrowsAsync<HttpRequestException>(() => s.Resolver.ResolveAsync(User("u1", "x"), default));
        var result = await s.Resolver.ResolveAsync(User("u1", "x"), default);

        Assert.Contains(("building", "B1"), result);
    }

    /// <summary>
    /// One caller giving up (its request was cancelled) stops only its own wait; the shared resolution
    /// carries on for everyone else awaiting it.
    /// </summary>
    [Fact]
    public async Task ACancelledCaller_DoesNotCancelTheSharedResolution()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var s = Build(cache);
        Grant(s, "space", Known("R501"));
        var gate = new TaskCompletionSource<IReadOnlyCollection<(string, string)>>();
        s.Hierarchy.Setup(h => h.GetAncestorUnionAsync(
                "space", It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .Returns(gate.Task);
        using var cancelled = new CancellationTokenSource();

        var first = s.Resolver.ResolveAsync(User("u1", "x"), cancelled.Token);
        var second = NewResolver(s.Auth, s.Mapping, s.Hierarchy, cache).ResolveAsync(User("u1", "x"), default);
        cancelled.Cancel();
        gate.SetResult([("building", "B1")]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Contains(("building", "B1"), await second);
        s.Hierarchy.Verify(h => h.GetAncestorUnionAsync(
            "space", It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()), Times.Once());
    }

    /// <summary>The resolution never runs on the caller's token (which would let one request cancel it).</summary>
    [Fact]
    public async Task TheSharedResolution_IsNotGivenTheCallersToken()
    {
        var s = Build();
        Grant(s, "space", Known("R501"));
        using var callerToken = new CancellationTokenSource();

        await s.Resolver.ResolveAsync(User("u1", "x"), callerToken.Token);

        s.Hierarchy.Verify(h => h.GetAncestorUnionAsync(
            "space", It.IsAny<IReadOnlyCollection<string>>(), callerToken.Token), Times.Never());
    }
}
