using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using BuildingOS.Shared.Infrastructure.OxiGraph;
using BuildingOS.Shared.Module;
using BuildingOS.Shared.Module.Oss;

namespace BuildingOS.Shared.Test.Module;

/// <summary>
/// Unit coverage for the flattened point-metadata load (#371).
///
/// <para>
/// The single bulk SPARQL this data source used to run is quadratic in point count — the
/// <c>OPTIONAL { ?equip a sbco:EquipmentExt ; sbco:hasPoint ?point ; sbco:id ?deviceId }</c> term is
/// the quadratic factor (23 s at 1,865 points, 57 s at 3,000, and past .NET's 100 s HttpClient
/// default under load). The fix splits it into three linear queries — point projection, point→device
/// link, building reachability — joined in memory by a pure function.
/// </para>
///
/// <para>
/// These tests pin the two halves that can be checked without a graph database: the SPARQL each
/// query must and must not contain (so nobody re-introduces the quadratic OPTIONAL, and nobody
/// "optimizes" the device-link query by dropping the <c>a sbco:EquipmentExt</c> type triple, which
/// would change semantics), and the in-memory merge. The traversal semantics themselves stay guarded
/// by the real-OxiGraph integration tests (<c>PointMetadataBuildingPathTest</c>,
/// <c>DirectLevelHierarchyTest</c>) — the merge fixtures below deliberately mirror their twin shapes.
/// </para>
/// </summary>
public class OxiGraphPointMetadataDataSourceTest
{
    private const string EmptyResults = @"{ ""results"": { ""bindings"": [] } }";

    // ---------------------------------------------------------------------------------------------
    // 1. The pure merge joins by point id.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Mirrors <c>PointMetadataBuildingPathTest.SpatialChain_IsReachable</c>: the point projection
    /// supplies building/name/gatewayId, the device-link query supplies DeviceId, and the
    /// reachability set supplies HasBuildingPath — all keyed by point id.
    /// </summary>
    [Fact]
    public void Merge_JoinsDeviceIdAndBuildingPathByPointId()
    {
        var result = Sut.Merge(
            points: [Point("PT001", building: "bldg-1", name: "Room Temp", gatewayId: "gw-1")],
            deviceLinks: [Link("PT001", "DEV001")],
            reachable: [Reachable("PT001")]);

        var meta = Assert.Single(result);
        Assert.Equal("PT001", meta.PointId);
        Assert.Equal("bldg-1", meta.Building);
        Assert.Equal("Room Temp", meta.Name);
        Assert.Equal("gw-1", meta.GatewayId);
        Assert.Equal("DEV001", meta.DeviceId);
        Assert.True(meta.HasBuildingPath);
    }

    /// <summary>
    /// The point projection is the only row driver: device links and reachability rows naming a point
    /// the projection did not return must not conjure a row (they would carry no building/name/gateway).
    /// </summary>
    [Fact]
    public void Merge_IgnoresDeviceLinksAndReachabilityForPointsTheProjectionDidNotReturn()
    {
        var result = Sut.Merge(
            points: [Point("PT001")],
            deviceLinks: [Link("PT001", "DEV001"), Link("PT-GHOST", "DEV999")],
            reachable: [Reachable("PT001"), Reachable("PT-GHOST")]);

        var meta = Assert.Single(result);
        Assert.Equal("PT001", meta.PointId);
    }

    // ---------------------------------------------------------------------------------------------
    // 2. No device link → empty DeviceId, never a dropped row.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Mirrors <c>PointMetadataBuildingPathTest.PointWithNoEquipment_IsNotReachable</c>. Today the
    /// device join is an OPTIONAL, so an unowned point still produces a row with an unbound
    /// <c>?deviceId</c>, which <c>Get()</c> turns into <see cref="string.Empty"/>. Splitting the query
    /// out must not turn that OPTIONAL into an inner join.
    /// </summary>
    [Fact]
    public void Merge_PointWithNoDeviceLink_KeepsTheRowWithAnEmptyDeviceId()
    {
        var result = Sut.Merge(
            points: [Point("PT000", name: "Lonely")],
            deviceLinks: [],
            reachable: []);

        var meta = Assert.Single(result);
        Assert.Equal("PT000", meta.PointId);
        Assert.Equal(string.Empty, meta.DeviceId);
        Assert.False(meta.HasBuildingPath);
    }

    /// <summary>Unbound optionals stay <see cref="string.Empty"/>, never null (the <c>Get()</c> contract).</summary>
    [Fact]
    public void Merge_MissingOptionalBindingsBecomeEmptyStringsNotNull()
    {
        var result = Sut.Merge(points: [Point("PT001")], deviceLinks: [], reachable: []);

        var meta = Assert.Single(result);
        Assert.Equal(string.Empty, meta.Building);
        Assert.Equal(string.Empty, meta.Name);
        Assert.Equal(string.Empty, meta.GatewayId);
        Assert.Equal(string.Empty, meta.DeviceId);
    }

    // ---------------------------------------------------------------------------------------------
    // 3. HasBuildingPath is set membership.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Merge_HasBuildingPathIsDrivenByMembershipOfTheReachabilitySet()
    {
        var result = Sut.Merge(
            points: [Point("PT-IN"), Point("PT-OUT")],
            deviceLinks: [Link("PT-IN", "DEV-A"), Link("PT-OUT", "DEV-B")],
            reachable: [Reachable("PT-IN")]);

        Assert.True(Assert.Single(result, m => m.PointId == "PT-IN").HasBuildingPath);
        Assert.False(Assert.Single(result, m => m.PointId == "PT-OUT").HasBuildingPath);
    }

    /// <summary>
    /// Reachability is a property of the POINT, not of the (point, device) pair. The current query's
    /// hierarchy OPTIONAL binds its own <c>?anyDev sbco:hasPoint ?point</c> and never joins it to the
    /// <c>?equip</c> that supplied <c>?deviceId</c> — so a point owned by an unplaced device AND a
    /// placed one gets <c>HasBuildingPath == true</c> on BOTH rows. A merge that joined reachability
    /// per device link would get this backwards.
    /// </summary>
    [Fact]
    public void Merge_BuildingPathIsPerPointNotPerDeviceLink()
    {
        var result = Sut.Merge(
            points: [Point("PT001")],
            deviceLinks: [Link("PT001", "DEV-UNPLACED"), Link("PT001", "DEV-PLACED")],
            reachable: [Reachable("PT001")]);

        Assert.Equal(2, result.Length);
        Assert.All(result, m => Assert.True(m.HasBuildingPath));
    }

    // ---------------------------------------------------------------------------------------------
    // 4. Row multiplicity.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// <b>What the current code does.</b> The single query ends in
    /// <c>GROUP BY ?pointId ?building ?name ?gatewayId ?deviceId</c> with only <c>?bldg</c> aggregated
    /// (SAMPLE). <c>?deviceId</c> is therefore a grouping key, so a point owned by two
    /// <c>EquipmentExt</c> yields TWO solutions and hence two <see cref="PointMetadata"/> rows — one
    /// per device id. <c>PointMetadataCache.RefreshAsync</c> then collapses them with
    /// <c>GroupBy(PointId).ToDictionary(g =&gt; g.Key, g =&gt; g.Last())</c>, so the LAST row wins.
    ///
    /// <para>
    /// <b>Why this expectation matches.</b> The current SPARQL has no ORDER BY, so which of the two
    /// device ids is "last" is whatever solution order OxiGraph happens to produce — unspecified, and
    /// therefore nothing a merge can reproduce exactly. What must be preserved is (a) the row COUNT,
    /// so the cache still sees a choice rather than a silently pre-collapsed single row, and (b) a
    /// DETERMINISTIC order, which the merge can offer and the old query could not: rows for a point
    /// appear in the order their device links arrived, so <c>Last()</c> picks the last device link the
    /// device-link query returned. Collapsing to one row per point would change which value wins and
    /// is the regression this test exists to catch.
    /// </para>
    /// </summary>
    [Fact]
    public void Merge_PointWithTwoDeviceLinks_YieldsOneRowPerLinkInDeviceLinkOrder()
    {
        var result = Sut.Merge(
            points: [Point("PT001", name: "Room Temp")],
            deviceLinks: [Link("PT001", "DEV-A"), Link("PT001", "DEV-B")],
            reachable: [Reachable("PT001")]);

        Assert.Equal(2, result.Length);
        Assert.Equal(new[] { "DEV-A", "DEV-B" }, result.Select(m => m.DeviceId));

        // Exactly what PointMetadataCache.RefreshAsync does with the array.
        var collapsed = result.GroupBy(m => m.PointId)
                              .ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        Assert.Equal("DEV-B", collapsed["PT001"].DeviceId);
    }

    /// <summary>
    /// The multiplicity is the CROSS PRODUCT of point-projection rows and device links, because
    /// <c>?name</c> (like <c>?building</c> and <c>?gatewayId</c>) is also a GROUP BY key today: a point
    /// carrying two <c>sbco:name</c> literals and owned by two devices produces four solutions.
    /// </summary>
    [Fact]
    public void Merge_MultiplicityIsTheCrossProductOfPointRowsAndDeviceLinks()
    {
        var result = Sut.Merge(
            points: [Point("PT001", name: "Name A"), Point("PT001", name: "Name B")],
            deviceLinks: [Link("PT001", "DEV-A"), Link("PT001", "DEV-B")],
            reachable: []);

        Assert.Equal(4, result.Length);
        Assert.Equal(
            new[] { ("Name A", "DEV-A"), ("Name A", "DEV-B"), ("Name B", "DEV-A"), ("Name B", "DEV-B") },
            result.Select(m => (m.Name, m.DeviceId)));
    }

    // ---------------------------------------------------------------------------------------------
    // 5. Empty point ids are dropped.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The current code ends with <c>.Where(m =&gt; !string.IsNullOrEmpty(m.PointId))</c>. A row whose
    /// <c>?pointId</c> is unbound (key absent) or bound to the empty literal must not survive, and a
    /// device link / reachability row with an empty point id must not attach itself to anything.
    /// </summary>
    [Fact]
    public void Merge_DropsRowsWithAnEmptyPointId()
    {
        var result = Sut.Merge(
            points:
            [
                new Dictionary<string, string>(StringComparer.Ordinal) { ["name"] = "no id at all" },
                Point("", name: "empty id"),
                Point("PT001"),
            ],
            deviceLinks: [Link("", "DEV-GHOST")],
            reachable: [Reachable("")]);

        var meta = Assert.Single(result);
        Assert.Equal("PT001", meta.PointId);
        Assert.Equal(string.Empty, meta.DeviceId);
        Assert.False(meta.HasBuildingPath);
    }

    // ---------------------------------------------------------------------------------------------
    // 6. #292: HasBuildingPath is NOT the sbco:building literal. The single most important invariant.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Mirrors <c>PointMetadataBuildingPathTest.StaleBuildingLiteral_IsNotReachable</c>: a point with a
    /// stale/mistyped <c>sbco:building</c> literal that no building actually contains. The literal is
    /// still surfaced verbatim (it is the telemetry enrichment value and the Parquet partition key),
    /// but the hierarchy gate must say false.
    /// </summary>
    [Fact]
    public void Merge_BuildingLiteralPresentButNotReachable_IsNotABuildingPath()
    {
        var result = Sut.Merge(
            points: [Point("PT999", building: "bldg-typo", name: "Stray Point")],
            deviceLinks: [Link("PT999", "DEV999")],
            reachable: []);

        var meta = Assert.Single(result);
        Assert.False(meta.HasBuildingPath);
        Assert.Equal("bldg-typo", meta.Building);
    }

    /// <summary>
    /// Mirrors <c>PointMetadataBuildingPathTest.FloorLiteralJoin_IsReachable_EvenWithoutTheBuildingLiteral</c>
    /// — the THX shape. Reachable through the <c>sbco:floor</c> literal join with no <c>sbco:building</c>
    /// literal to show for it: HasBuildingPath must be true and Building must stay empty.
    /// </summary>
    [Fact]
    public void Merge_ReachableWithoutABuildingLiteral_IsStillABuildingPath()
    {
        var result = Sut.Merge(
            points: [Point("172_31_105_17-3002", name: "On/Off Status")],
            deviceLinks: [Link("172_31_105_17-3002", "172_31_105_17")],
            reachable: [Reachable("172_31_105_17-3002")]);

        var meta = Assert.Single(result);
        Assert.True(meta.HasBuildingPath);
        Assert.Equal(string.Empty, meta.Building);
        Assert.Equal("172_31_105_17", meta.DeviceId);
    }

    // ---------------------------------------------------------------------------------------------
    // 7. Query structure: three separate queries, quadratic term gone, semantics kept.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetAllAsync_IssuesThreeSeparateQueries()
    {
        var handler = new RecordingHandler(EmptyResults);
        await NewSource(handler).GetAllAsync();

        Assert.Equal(3, handler.Bodies.Count);
        Assert.Equal(3, handler.Bodies.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// The quadratic term. <c>EquipmentExt</c> must not appear anywhere in the point projection —
    /// that OPTIONAL is the whole reason the load went from 0.04 s to 11 s at 1,000 points.
    /// The projection also must not carry the hierarchy UNION.
    /// </summary>
    [Fact]
    public async Task PointProjectionQuery_HasNeitherTheEquipmentJoinNorTheHierarchyUnion()
    {
        var q = await CaptureQueriesAsync();

        Assert.Contains("?point a sbco:PointExt", q.PointProjection);
        Assert.Contains("sbco:building", q.PointProjection);
        Assert.Contains("sbco:name", q.PointProjection);
        Assert.DoesNotContain("EquipmentExt", q.PointProjection);
        Assert.DoesNotContain("sbco:hasPoint", q.PointProjection);
        Assert.DoesNotContain("UNION", q.PointProjection);
    }

    /// <summary>
    /// Candidate E2 (drop the <c>a sbco:EquipmentExt</c> triple) was just as fast as E3 and was
    /// rejected because it changes semantics — it would link a point to any subject with
    /// <c>sbco:hasPoint</c>, not just equipment. E3 keeps the type check and is fast because the
    /// pattern is driven from the equipment side rather than sitting inside an OPTIONAL. This test
    /// exists so a later "optimization" cannot quietly delete the type triple.
    /// </summary>
    [Fact]
    public async Task DeviceLinkQuery_KeepsTheEquipmentExtTypeTripleAndIsEquipmentDriven()
    {
        var q = await CaptureQueriesAsync();

        Assert.Contains("a sbco:EquipmentExt", q.DeviceLink);
        Assert.Contains("sbco:hasPoint", q.DeviceLink);
        Assert.Contains("?deviceId", q.DeviceLink);
        // Equipment-driven (E3), not a point-driven OPTIONAL (E0/E1, the quadratic shape).
        Assert.DoesNotContain("OPTIONAL", q.DeviceLink);
    }

    /// <summary>
    /// All three #291/#292 reachability branches must survive the split: the Room spatial chain, the
    /// direct Level location (#319), and the <c>sbco:floor</c> literal join (the THX shape). Any path
    /// counts, so losing one silently turns reachable points into orphans at strict ingress.
    /// </summary>
    [Fact]
    public async Task ReachabilityQuery_KeepsAllThreeUnionBranches()
    {
        var q = await CaptureQueriesAsync();

        Assert.Equal(2, Regex.Matches(q.Reachability, @"\bUNION\b").Count);   // 2 UNIONs = 3 branches
        Assert.Contains("DISTINCT", q.Reachability);
        Assert.Contains("sbco:hasPoint", q.Reachability);
        Assert.Contains("sbco:locatedIn", q.Reachability);
        Assert.Contains("a sbco:Room", q.Reachability);
        Assert.Contains("a sbco:Level", q.Reachability);
        Assert.Contains("a sbco:Building", q.Reachability);
        Assert.Contains("sbco:floor", q.Reachability);
        Assert.Contains("sbco:hasPart", q.Reachability);
    }

    // ---------------------------------------------------------------------------------------------
    // 7b. The id join must not leak across nodes that merely share an sbco:id.
    //
    // The old single query bound ONE ?point, typed `a sbco:PointExt` once, and BOTH optionals joined
    // it by node identity. Three queries cannot share a variable, so they re-join on the id literal —
    // a weaker key. Each must therefore re-assert the type constraint the shared node used to carry.
    //
    // Verified against a live OxiGraph with this twin:
    //   <pt>    a sbco:PointExt ; sbco:id "COL" .            # orphan: no equipment, no placement
    //   <ghost> a sbco:Thing    ; sbco:id "COL" .            # NOT a point
    //   <eq>    a sbco:EquipmentExt ; sbco:id "DEV-GHOST" ;
    //           sbco:hasPoint <ghost> ; sbco:locatedIn <room under a Building> .
    // old query          -> ("COL", deviceId "",           HasBuildingPath false)
    // untyped split      -> ("COL", deviceId "DEV-GHOST",  HasBuildingPath TRUE)   <- the regression
    // split + type triples -> identical to old.
    // The type triples cost ~10 ms at 3,000 points (device link 0.035 s -> 0.042 s, reachability
    // 0.033 s -> 0.044 s): the quadratic term was the OPTIONAL, never the type check.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The <c>sbco:hasPoint</c> OBJECT must be typed too, not just the <c>?equip</c> subject. Without
    /// it an <c>E1 sbco:hasPoint E2</c> edge between two EquipmentExt — which also carry
    /// <c>sbco:id</c> — puts a device id into the point-id keyspace, and a real point whose id
    /// collides inherits the wrong <see cref="PointMetadata.DeviceId"/>: the value enriched onto every
    /// telemetry frame for that point and written into the Parquet lake.
    /// </summary>
    [Fact]
    public async Task DeviceLinkQuery_ConstrainsTheLinkedPointToPointExt()
    {
        var q = await CaptureQueriesAsync();

        Assert.Contains("?pt a sbco:PointExt", q.DeviceLink);
    }

    /// <summary>
    /// The reachability set is a statement about POINTS. Without the type triple any
    /// <c>sbco:hasPoint</c> object carrying a colliding <c>sbco:id</c> confers a building path on a
    /// genuine orphan — a fail-OPEN of the #292 ingress gate, and a direct contradiction of #291,
    /// whose <c>OxiGraphTwinAdminService.OrphanPattern</c> anchors on <c>?pt a sbco:PointExt</c> by
    /// node and still reports that point as an orphan.
    /// </summary>
    [Fact]
    public async Task ReachabilityQuery_ConstrainsThePointToPointExt()
    {
        var q = await CaptureQueriesAsync();

        Assert.Contains("?point a sbco:PointExt", q.Reachability);
    }

    // ---------------------------------------------------------------------------------------------
    // 7c. The residual case the id join cannot reproduce: one id on several PointExt NODES.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Two PointExt nodes sharing one <c>sbco:id</c> — one placed under a building, one an orphan.
    /// Live comparison on that twin:
    /// <code>
    /// old (node join): ("DUP","placed","DEV-A",true), ("DUP","orphan","DEV-B",false)
    /// new (id join):   both device ids on both rows, HasBuildingPath true on all four
    /// </code>
    /// <b>This divergence is deliberate.</b> The old rows were only "independent" until
    /// <c>PointMetadataCache</c> collapsed them with <c>GroupBy(PointId).Last()</c>, and the query has
    /// no ORDER BY — so which of true/false won was unspecified solution order, not a behaviour to
    /// preserve. Restoring it would mean joining all three queries by the point TERM, whose value for a
    /// blank-node point is a bnode label that SPARQL scopes to one result set; this OxiGraph build
    /// happens to emit stable labels, but the day it did not, a blank-node point would silently lose
    /// its device id and its building path and be dropped at strict ingress. So the id join stands and
    /// the consequence is pinned here: point id is the ingress key, and a point id the twin places
    /// under a building anywhere is reachable — deterministically, rather than by luck.
    /// <see cref="Sut.AmbiguousPointIds"/> makes the twin defect visible in the log.
    /// </summary>
    [Fact]
    public void Merge_TwoPointNodesSharingOneId_AreIndistinguishableAndUnionTheirBuildingPath()
    {
        var result = Sut.Merge(
            points: [Point("DUP", name: "placed", node: "urn:pt:a"), Point("DUP", name: "orphan", node: "urn:pt:b")],
            deviceLinks: [Link("DUP", "DEV-A"), Link("DUP", "DEV-B")],
            reachable: [Reachable("DUP")]);   // the placed node's reachability, keyed only by the id

        Assert.Equal(4, result.Length);
        Assert.All(result, m => Assert.True(m.HasBuildingPath));
    }

    /// <summary>One id on several nodes is a twin defect the operator must be told about, by id.</summary>
    [Fact]
    public void AmbiguousPointIds_ReportsIdsCarriedByMoreThanOnePointNode()
    {
        var ambiguous = Sut.AmbiguousPointIds(
        [
            Point("DUP", name: "placed", node: "urn:pt:a"),
            Point("DUP", name: "orphan", node: "urn:pt:b"),
            Point("FINE", name: "one node", node: "urn:pt:c"),
        ]);

        Assert.Equal(new[] { "DUP" }, ambiguous);
    }

    /// <summary>
    /// A point carrying two <c>sbco:name</c> literals also produces two projection rows, but they are
    /// the SAME node — that is ordinary multiplicity, not an ambiguous id, and must not be reported.
    /// </summary>
    [Fact]
    public void AmbiguousPointIds_DoesNotReportOneNodeSpreadOverSeveralRows()
    {
        var ambiguous = Sut.AmbiguousPointIds(
        [
            Point("PT001", name: "Name A", node: "urn:pt:a"),
            Point("PT001", name: "Name B", node: "urn:pt:a"),
        ]);

        Assert.Empty(ambiguous);
    }

    /// <summary>The point projection must select the term the ambiguity check needs.</summary>
    [Fact]
    public async Task PointProjectionQuery_SelectsThePointTermSoAmbiguousIdsCanBeDetected()
    {
        var q = await CaptureQueriesAsync();

        Assert.Contains("SELECT ?point ?pointId", q.PointProjection);
    }

    // ---------------------------------------------------------------------------------------------
    // 8. Per-call timeout.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The shared <c>AddHttpClient("oxigraph")</c> configures no timeout, so .NET's 100 s default
    /// applies — long enough that a slow load blocks every parked ingress stream (#371) instead of
    /// failing fast into <c>PointMetadataCache.LoadWithRetryAsync</c>'s retry. This data source must
    /// bound its OWN calls with a linked CTS. Proven behaviourally: with a 250 ms override the call
    /// gives up in well under the HttpClient default, and the caller's token is NOT the thing that
    /// cancelled it.
    /// </summary>
    [Fact]
    public async Task GetAllAsync_BoundsEachQueryWithItsOwnTimeout()
    {
        var handler = new HangingHandler();
        var source = Sut.Create(NewClient(handler), TimeSpan.FromMilliseconds(250));

        using var callerCts = new CancellationTokenSource();
        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.GetAllAsync(callerCts.Token));
        sw.Stop();

        Assert.False(callerCts.IsCancellationRequested);           // not the caller giving up
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20),          // nowhere near HttpClient's 100 s
            $"expected the data source's own timeout to fire, but the call took {sw.Elapsed}");
        Assert.True(handler.LastToken?.IsCancellationRequested == true);
    }

    /// <summary>
    /// Linked, not replaced: a caller that cancels must still abort its own call promptly.
    /// </summary>
    [Fact]
    public async Task GetAllAsync_StillHonoursTheCallerToken()
    {
        var handler = new HangingHandler();
        var source = Sut.Create(NewClient(handler), TimeSpan.FromSeconds(30));

        using var callerCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.GetAllAsync(callerCts.Token));
        sw.Stop();

        Assert.True(callerCts.IsCancellationRequested);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20),
            $"caller cancellation should propagate immediately, but the call took {sw.Elapsed}");
    }

    /// <summary>
    /// The bound is PER CALL, not one budget spanning the whole load — three queries each comfortably
    /// inside the timeout must succeed even when their total exceeds it.
    /// </summary>
    [Fact]
    public async Task GetAllAsync_TimeoutIsPerQueryNotForTheWholeLoad()
    {
        // 3 × 120 ms = 360 ms of query time against a 250 ms per-call bound.
        var handler = new RecordingHandler(EmptyResults, delay: TimeSpan.FromMilliseconds(120));
        var source = Sut.Create(NewClient(handler), TimeSpan.FromMilliseconds(250));

        var result = await source.GetAllAsync();

        Assert.Empty(result);
        Assert.Equal(3, handler.Bodies.Count);
    }

    /// <summary>
    /// ~100× headroom over the post-fix cost (~0.3 s at 3,000 points), and far below the 100 s default.
    /// </summary>
    [Fact]
    public void DefaultQueryTimeout_IsThirtySeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), Sut.DefaultQueryTimeout());
    }

    // ---------------------------------------------------------------------------------------------
    // Fixtures / helpers
    // ---------------------------------------------------------------------------------------------

    /// <param name="node">
    /// The <c>?point</c> term. Only the ambiguity check reads it (one id on several nodes); the merge
    /// ignores it, so the fixtures that are not about that case leave it unset.
    /// </param>
    private static IReadOnlyDictionary<string, string> Point(
        string pointId, string? building = null, string? name = null, string? gatewayId = null,
        string? node = null)
    {
        var row = new Dictionary<string, string>(StringComparer.Ordinal) { ["pointId"] = pointId };
        if (building is not null) row["building"] = building;
        if (name is not null) row["name"] = name;
        if (gatewayId is not null) row["gatewayId"] = gatewayId;
        if (node is not null) row["point"] = node;
        return row;
    }

    private static IReadOnlyDictionary<string, string> Link(string pointId, string deviceId)
        => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["pointId"] = pointId,
            ["deviceId"] = deviceId,
        };

    private static IReadOnlyDictionary<string, string> Reachable(string pointId)
        => new Dictionary<string, string>(StringComparer.Ordinal) { ["pointId"] = pointId };

    private static OxiGraphClient NewClient(HttpMessageHandler handler)
        // HttpClient.Timeout is left at the framework default (100 s) on purpose: the timeout tests
        // must prove the data source's own bound fired, not the HttpClient's.
        => new(new HttpClient(handler), "http://oxigraph:7878");

    private static IPointMetadataDataSource NewSource(HttpMessageHandler handler)
        => new OxiGraphPointMetadataDataSource(NewClient(handler));

    private sealed record Queries(string PointProjection, string DeviceLink, string Reachability);

    /// <summary>
    /// Runs a load against a recording handler and classifies the three captured SPARQL bodies by
    /// content: exactly one selects <c>?gatewayId</c> (the point projection), exactly one selects
    /// <c>?deviceId</c> (the device link), exactly one has a UNION (the reachability set) — which also
    /// asserts that the three concerns really are in three different queries.
    /// </summary>
    private static async Task<Queries> CaptureQueriesAsync()
    {
        var handler = new RecordingHandler(EmptyResults);
        await NewSource(handler).GetAllAsync();

        var bodies = handler.Bodies.Select(Normalize).ToList();
        Assert.Equal(3, bodies.Count);

        return new Queries(
            Single(bodies, "?gatewayId", "point projection"),
            Single(bodies, "?deviceId", "device link"),
            Single(bodies, "UNION", "building reachability"));

        static string Single(List<string> bodies, string marker, string what)
        {
            var hits = bodies.Where(b => b.Contains(marker, StringComparison.Ordinal)).ToList();
            Assert.True(hits.Count == 1,
                $"expected exactly one query to be the {what} (containing '{marker}'), found {hits.Count}");
            return hits[0];
        }
    }

    /// <summary>URL-decode the form body and collapse whitespace runs, so assertions do not depend on layout.</summary>
    private static string Normalize(string body)
    {
        var sparql = HttpUtility.ParseQueryString(body)["query"] ?? HttpUtility.UrlDecode(body);
        return Regex.Replace(sparql, @"\s+", " ").Trim();
    }

    private sealed class RecordingHandler(string body, TimeSpan delay = default) : HttpMessageHandler
    {
        private readonly List<string> _bodies = [];

        public IReadOnlyList<string> Bodies
        {
            get { lock (_bodies) return _bodies.ToArray(); }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var captured = request.Content is not null ? await request.Content.ReadAsStringAsync(ct) : string.Empty;
            lock (_bodies) _bodies.Add(captured);
            if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/sparql-results+json"),
            };
        }
    }

    /// <summary>Never answers; records the token it was handed so a test can see what cancelled it.</summary>
    private sealed class HangingHandler : HttpMessageHandler
    {
        public CancellationToken? LastToken { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastToken = ct;
            await Task.Delay(Timeout.Infinite, ct);
            throw new UnreachableException();
        }
    }
}

/// <summary>
/// RED-phase seam. The members these tests need do not exist on
/// <see cref="OxiGraphPointMetadataDataSource"/> yet, so they are reached by reflection — otherwise
/// the test project would not compile and the rest of the unit suite could not run. Each lookup
/// fails with the exact signature the GREEN phase must add.
///
/// <para>
/// Once GREEN lands, this shim can be replaced by direct calls; the tests above are written so that
/// only the bodies of these three helpers change.
/// </para>
/// </summary>
internal static class Sut
{
    private const BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Type Target = typeof(OxiGraphPointMetadataDataSource);

    /// <summary>
    /// GREEN must add:
    /// <c>internal static PointMetadata[] Merge(
    ///   IReadOnlyList&lt;IReadOnlyDictionary&lt;string,string&gt;&gt; pointRows,
    ///   IReadOnlyList&lt;IReadOnlyDictionary&lt;string,string&gt;&gt; deviceLinkRows,
    ///   IReadOnlyList&lt;IReadOnlyDictionary&lt;string,string&gt;&gt; reachableRows)</c>
    /// — pure and static, so the join can be tested without a graph database.
    /// </summary>
    internal static PointMetadata[] Merge(
        IReadOnlyList<IReadOnlyDictionary<string, string>> points,
        IReadOnlyList<IReadOnlyDictionary<string, string>> deviceLinks,
        IReadOnlyList<IReadOnlyDictionary<string, string>> reachable)
    {
        var method = Target.GetMethods(AnyStatic)
            .FirstOrDefault(m => m.Name == "Merge" && m.GetParameters().Length == 3);
        Assert.True(method is not null,
            $"{Target.Name} must expose a pure static 3-argument Merge(pointRows, deviceLinkRows, reachableRows) " +
            "returning PointMetadata[] (#371).");

        object? raw;
        try
        {
            raw = method!.Invoke(null, [points, deviceLinks, reachable]);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }

        return Assert.IsType<PointMetadata[]>(raw);
    }

    /// <summary>
    /// GREEN must add a second constructor parameter bounding each SPARQL call, e.g.
    /// <c>OxiGraphPointMetadataDataSource(OxiGraphClient client, TimeSpan? queryTimeout = null)</c>.
    /// </summary>
    internal static IPointMetadataDataSource Create(OxiGraphClient client, TimeSpan queryTimeout)
    {
        var ctor = Target.GetConstructors()
            .FirstOrDefault(c => c.GetParameters() is { Length: 2 } p
                && p[0].ParameterType == typeof(OxiGraphClient)
                && (p[1].ParameterType == typeof(TimeSpan) || p[1].ParameterType == typeof(TimeSpan?)));
        Assert.True(ctor is not null,
            $"{Target.Name} must accept an overridable per-call query timeout, e.g. " +
            "ctor(OxiGraphClient client, TimeSpan? queryTimeout = null) (#371).");

        return (IPointMetadataDataSource)ctor!.Invoke([client, queryTimeout]);
    }

    /// <summary>
    /// Added after GREEN landed, so it is a direct call rather than a reflection lookup — the assembly
    /// grants <c>InternalsVisibleTo("BuildingOS.Shared.Test")</c>, and a compile error is a better
    /// signal than a runtime assert for a member that already exists.
    /// </summary>
    internal static string[] AmbiguousPointIds(IReadOnlyList<IReadOnlyDictionary<string, string>> points)
        => OxiGraphPointMetadataDataSource.AmbiguousPointIds(points);

    /// <summary>GREEN must expose the default as <c>DefaultQueryTimeout</c> (static field or property).</summary>
    internal static TimeSpan? DefaultQueryTimeout()
    {
        var value = Target.GetField("DefaultQueryTimeout", AnyStatic)?.GetValue(null)
                    ?? Target.GetProperty("DefaultQueryTimeout", AnyStatic)?.GetValue(null);
        Assert.True(value is TimeSpan,
            $"{Target.Name} must expose a static DefaultQueryTimeout (#371); found: {value?.GetType().Name ?? "nothing"}");
        return (TimeSpan)value!;
    }
}
