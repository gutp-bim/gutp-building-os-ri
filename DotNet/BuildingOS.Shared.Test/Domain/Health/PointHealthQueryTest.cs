using BuildingOS.Shared.Domain.Health;

namespace BuildingOS.Shared.Test.Domain.Health;

/// <summary>
/// データ健全性一覧（#452）の絞り込み・並べ替え・ページングの仕様。
///
/// <para>ここは <b>純粋関数</b>。台帳（OxiGraph）にも index にも触らず、
/// <see cref="PointHealthItem"/> の配列を受けて配列を返すだけにしてある。判定済みの 3 軸に対する
/// 絞り込みは SPARQL では書けない（鮮度は台帳に無い）ので、コントローラが「台帳 × index → 判定」
/// まで済ませたものをこの関数に渡す、という分担にしている。</para>
///
/// <para><b>total はページング前の件数</b>。画面のページャと「◯件が該当」表示がこの値を使う。</para>
/// </summary>
public class PointHealthQueryTest
{
    private static readonly DateTimeOffset Now = new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);

    private static PointHealthItem Item(
        string pointId,
        HealthStatus health = HealthStatus.Fresh,
        FreshnessStatus freshness = FreshnessStatus.Fresh,
        AlarmStatus alarm = AlarmStatus.Normal,
        long? ageSeconds = 0,
        DateTimeOffset? lastSeen = null,
        string? name = null,
        string? buildingDtId = null,
        string? floorDtId = null,
        string? deviceDtId = null,
        string? gatewayId = null,
        string[]? tags = null) => new()
        {
            PointId = pointId,
            PointDtId = "urn:dtid:" + pointId,
            Name = name ?? pointId,
            Unit = "Cel",
            Freshness = new PointFreshnessResult
            {
                Status = freshness,
                LastSeen = lastSeen,
                AgeSeconds = ageSeconds,
                ThresholdSeconds = 900,
                ThresholdSource = ThresholdSource.System,
            },
            Alarm = new PointAlarmResult { Status = alarm, Value = 23.4 },
            Gateway = gatewayId is null ? null : new PointGatewayInfo(gatewayId, true),
            HealthStatus = health,
            BuildingDtId = buildingDtId,
            FloorDtId = floorDtId,
            DeviceDtId = deviceDtId,
            Tags = tags ?? Array.Empty<string>(),
        };

    private static PointHealthQuery Query() => new();

    private static string[] Ids(PointHealthPage page) => page.Items.Select(i => i.PointId).ToArray();

    // ---------------------------------------------------------------------
    // 絞り込みなし
    // ---------------------------------------------------------------------

    [Fact]
    public void Apply_NoFilters_PassesEverythingThrough()
    {
        var page = PointHealthQueryFilter.Apply(
            [Item("PT-1"), Item("PT-2"), Item("PT-3")], Query());

        Assert.Equal(3, page.Total);
        Assert.Equal(3, page.Items.Count);
    }

    // ---------------------------------------------------------------------
    // 3 軸の絞り込み（同一軸の複数指定は OR）
    // ---------------------------------------------------------------------

    [Fact]
    public void Apply_FreshnessFilter_IsOrWithinTheAxis()
    {
        PointHealthItem[] items =
        [
            Item("fresh", freshness: FreshnessStatus.Fresh),
            Item("stale", freshness: FreshnessStatus.Stale),
            Item("missing", freshness: FreshnessStatus.Missing),
            Item("unknown", freshness: FreshnessStatus.Unknown),
        ];
        var query = Query();
        query.Freshness = [FreshnessStatus.Stale, FreshnessStatus.Missing];

        var page = PointHealthQueryFilter.Apply(items, query);

        Assert.Equal(2, page.Total);
        Assert.Contains("stale", Ids(page));
        Assert.Contains("missing", Ids(page));
    }

    [Fact]
    public void Apply_AlarmFilter_IsOrWithinTheAxis()
    {
        PointHealthItem[] items =
        [
            Item("normal", alarm: AlarmStatus.Normal),
            Item("warn", alarm: AlarmStatus.Warn),
            Item("critical", alarm: AlarmStatus.Critical),
            Item("suppressed", alarm: AlarmStatus.Suppressed),
        ];
        var query = Query();
        query.Alarm = [AlarmStatus.Warn, AlarmStatus.Critical];

        var page = PointHealthQueryFilter.Apply(items, query);

        Assert.Equal(2, page.Total);
        Assert.Contains("warn", Ids(page));
        Assert.Contains("critical", Ids(page));
    }

    [Fact]
    public void Apply_HealthStatusFilter_IsOrWithinTheAxis()
    {
        PointHealthItem[] items =
        [
            Item("c", health: HealthStatus.Critical),
            Item("w", health: HealthStatus.Warn),
            Item("f", health: HealthStatus.Fresh),
        ];
        var query = Query();
        query.HealthStatuses = [HealthStatus.Critical, HealthStatus.Warn];

        var page = PointHealthQueryFilter.Apply(items, query);

        Assert.Equal(2, page.Total);
        Assert.DoesNotContain("f", Ids(page));
    }

    [Fact]
    public void Apply_DifferentAxesAreAnded()
    {
        // 軸をまたぐ指定は AND（「鮮度切れ かつ 警報あり」を出したいので）。
        PointHealthItem[] items =
        [
            Item("both", freshness: FreshnessStatus.Stale, alarm: AlarmStatus.Warn),
            Item("staleOnly", freshness: FreshnessStatus.Stale, alarm: AlarmStatus.Normal),
            Item("warnOnly", freshness: FreshnessStatus.Fresh, alarm: AlarmStatus.Warn),
        ];
        var query = Query();
        query.Freshness = [FreshnessStatus.Stale];
        query.Alarm = [AlarmStatus.Warn];

        var page = PointHealthQueryFilter.Apply(items, query);

        Assert.Equal("both", Assert.Single(page.Items).PointId);
    }

    // ---------------------------------------------------------------------
    // olderThan（欠測を取りこぼさないことが要点）
    // ---------------------------------------------------------------------

    [Fact]
    public void Apply_OlderThan_MatchesAtAndAboveTheThreshold()
    {
        PointHealthItem[] items =
        [
            Item("young", ageSeconds: 899),
            Item("exact", ageSeconds: 900),
            Item("old", ageSeconds: 901),
        ];
        var query = Query();
        query.OlderThanSeconds = 900;

        var page = PointHealthQueryFilter.Apply(items, query);

        Assert.Equal(2, page.Total);
        Assert.DoesNotContain("young", Ids(page));
    }

    [Fact]
    public void Apply_OlderThan_AlwaysMatchesPointsWithNoLastSeen()
    {
        // 「一度も来ていない」Point は齢が ∞。ここを取りこぼすと「30 分以上来ていないもの」を
        // 探した運用者に、最も深刻な欠測だけが見えなくなる。
        PointHealthItem[] items =
        [
            Item("never", freshness: FreshnessStatus.Missing, ageSeconds: null, lastSeen: null),
            Item("young", ageSeconds: 10),
        ];
        var query = Query();
        query.OlderThanSeconds = 900;

        var page = PointHealthQueryFilter.Apply(items, query);

        Assert.Equal("never", Assert.Single(page.Items).PointId);
    }

    // ---------------------------------------------------------------------
    // タグ・フリーワード・階層
    // ---------------------------------------------------------------------

    [Fact]
    public void Apply_Tags_AreAnded()
    {
        PointHealthItem[] items =
        [
            Item("both", tags: ["critical", "hvac"]),
            Item("one", tags: ["critical"]),
            Item("none", tags: []),
        ];
        var query = Query();
        query.Tags = ["critical", "hvac"];

        var page = PointHealthQueryFilter.Apply(items, query);

        Assert.Equal("both", Assert.Single(page.Items).PointId);
    }

    [Fact]
    public void Apply_Tags_AreCaseInsensitive()
    {
        var query = Query();
        query.Tags = ["Critical"];

        var page = PointHealthQueryFilter.Apply([Item("p", tags: ["critical"])], query);

        Assert.Single(page.Items);
    }

    [Fact]
    public void Apply_Q_MatchesPointIdOrNameCaseInsensitively()
    {
        PointHealthItem[] items =
        [
            Item("SAT-001", name: "給気温度"),
            Item("RAT-002", name: "還気温度 SAT 参考"),
            Item("PT-003", name: "湿度"),
        ];
        var query = Query();
        query.Q = "sat";

        var page = PointHealthQueryFilter.Apply(items, query);

        Assert.Equal(2, page.Total);
        Assert.DoesNotContain("PT-003", Ids(page));
    }

    [Fact]
    public void Apply_Q_IsTrimmedAndBlankMeansNoFilter()
    {
        var items = new[] { Item("SAT-001"), Item("PT-002") };

        var trimmed = Query();
        trimmed.Q = "  SAT  ";
        Assert.Equal("SAT-001", Assert.Single(PointHealthQueryFilter.Apply(items, trimmed).Items).PointId);

        var blank = Query();
        blank.Q = "   ";
        Assert.Equal(2, PointHealthQueryFilter.Apply(items, blank).Total);
    }

    [Fact]
    public void Apply_ScopeFilters_NarrowByHierarchy()
    {
        PointHealthItem[] items =
        [
            Item("a", buildingDtId: "urn:dtid:b1", floorDtId: "urn:dtid:f1", deviceDtId: "urn:dtid:d1"),
            Item("b", buildingDtId: "urn:dtid:b1", floorDtId: "urn:dtid:f2", deviceDtId: "urn:dtid:d2"),
            Item("c", buildingDtId: "urn:dtid:b2", floorDtId: "urn:dtid:f3", deviceDtId: "urn:dtid:d3"),
        ];

        var byBuilding = Query();
        byBuilding.BuildingDtId = "urn:dtid:b1";
        Assert.Equal(2, PointHealthQueryFilter.Apply(items, byBuilding).Total);

        var byFloor = Query();
        byFloor.FloorDtId = "urn:dtid:f2";
        Assert.Equal("b", Assert.Single(PointHealthQueryFilter.Apply(items, byFloor).Items).PointId);

        var byDevice = Query();
        byDevice.DeviceDtId = "urn:dtid:d3";
        Assert.Equal("c", Assert.Single(PointHealthQueryFilter.Apply(items, byDevice).Items).PointId);
    }

    [Fact]
    public void Apply_GatewayFilter_IsCaseInsensitiveAndSkipsPointsWithNoGateway()
    {
        // gateway id の照合は registry と同じく大小無視（CLAUDE.md の gateway 設定ルックアップ規約）。
        PointHealthItem[] items =
        [
            Item("a", gatewayId: "GW-001"),
            Item("b", gatewayId: "GW-002"),
            Item("c"),
        ];
        var query = Query();
        query.GatewayId = "gw-001";

        var page = PointHealthQueryFilter.Apply(items, query);

        Assert.Equal("a", Assert.Single(page.Items).PointId);
    }

    // ---------------------------------------------------------------------
    // 並べ替え
    // ---------------------------------------------------------------------

    [Fact]
    public void Apply_SortWorst_OrdersBySeverityDeclarationOrder()
    {
        PointHealthItem[] items =
        [
            Item("fresh", health: HealthStatus.Fresh),
            Item("unknown", health: HealthStatus.Unknown),
            Item("stale", health: HealthStatus.Stale),
            Item("missing", health: HealthStatus.Missing),
            Item("warn", health: HealthStatus.Warn),
            Item("critical", health: HealthStatus.Critical),
        ];
        var query = Query();
        query.Sort = PointHealthSort.Worst;

        var page = PointHealthQueryFilter.Apply(items, query);

        Assert.Equal(["critical", "warn", "missing", "stale", "unknown", "fresh"], Ids(page));
    }

    [Fact]
    public void Apply_SortWorst_IsTheDefault()
    {
        var page = PointHealthQueryFilter.Apply(
            [Item("fresh", health: HealthStatus.Fresh), Item("critical", health: HealthStatus.Critical)],
            Query());

        Assert.Equal(["critical", "fresh"], Ids(page));
    }

    [Fact]
    public void Apply_SortWorst_BreaksTiesByAgeDescendingThenName()
    {
        // 同じ深刻度なら「より長く来ていない方」が上。齢が無い（一度も来ていない）ものは ∞ 扱いで先頭。
        PointHealthItem[] items =
        [
            Item("young", health: HealthStatus.Stale, ageSeconds: 100, name: "B"),
            Item("old", health: HealthStatus.Stale, ageSeconds: 5000, name: "C"),
            Item("never", health: HealthStatus.Stale, ageSeconds: null, name: "D"),
        ];
        var query = Query();
        query.Sort = PointHealthSort.Worst;

        Assert.Equal(["never", "old", "young"], Ids(PointHealthQueryFilter.Apply(items, query)));

        PointHealthItem[] sameAge =
        [
            Item("z", health: HealthStatus.Stale, ageSeconds: 100, name: "Zebra"),
            Item("a", health: HealthStatus.Stale, ageSeconds: 100, name: "Alpha"),
        ];
        Assert.Equal(["a", "z"], Ids(PointHealthQueryFilter.Apply(sameAge, query)));
    }

    [Fact]
    public void Apply_SortLastSeen_IsOldestFirstWithNeverReceivedOnTop()
    {
        // 「最終受信が古い順」を明示的な仕様として固定する（新しい順ではない）。健全性画面で
        // 見たいのは「放置されている点」で、lastSeen 無しはその極北なので先頭に置く。
        PointHealthItem[] items =
        [
            Item("recent", lastSeen: Now.AddMinutes(-1), ageSeconds: 60),
            Item("older", lastSeen: Now.AddHours(-5), ageSeconds: 18_000),
            Item("never", lastSeen: null, ageSeconds: null, freshness: FreshnessStatus.Missing),
        ];
        var query = Query();
        query.Sort = PointHealthSort.LastSeen;

        Assert.Equal(["never", "older", "recent"], Ids(PointHealthQueryFilter.Apply(items, query)));
    }

    [Fact]
    public void Apply_SortName_IsAscendingCaseInsensitive()
    {
        PointHealthItem[] items =
        [
            Item("p3", name: "還気温度"),
            Item("p1", name: "alpha"),
            Item("p2", name: "Beta"),
        ];
        var query = Query();
        query.Sort = PointHealthSort.Name;

        Assert.Equal(["p1", "p2", "p3"], Ids(PointHealthQueryFilter.Apply(items, query)));
    }

    [Fact]
    public void Apply_SortName_FallsBackToPointIdWhenNameIsBlank()
    {
        PointHealthItem[] items =
        [
            Item("zzz", name: ""),
            Item("aaa", name: null),
        ];
        var query = Query();
        query.Sort = PointHealthSort.Name;

        Assert.Equal(["aaa", "zzz"], Ids(PointHealthQueryFilter.Apply(items, query)));
    }

    // ---------------------------------------------------------------------
    // ページング
    // ---------------------------------------------------------------------

    [Fact]
    public void Apply_LimitAndOffset_PageWithinTheSortedResult()
    {
        var items = Enumerable.Range(1, 10)
            .Select(i => Item($"PT-{i:00}", health: HealthStatus.Stale, ageSeconds: 1000 - i, name: $"PT-{i:00}"))
            .ToArray();
        var query = Query();
        query.Limit = 3;
        query.Offset = 3;

        var page = PointHealthQueryFilter.Apply(items, query);

        Assert.Equal(["PT-04", "PT-05", "PT-06"], Ids(page));
        Assert.Equal(10, page.Total);
    }

    [Fact]
    public void Apply_Total_IsCountedBeforePaging()
    {
        var items = Enumerable.Range(1, 7).Select(i => Item($"PT-{i}")).ToArray();
        var query = Query();
        query.Limit = 2;

        var page = PointHealthQueryFilter.Apply(items, query);

        Assert.Equal(2, page.Items.Count);
        Assert.Equal(7, page.Total);
    }

    [Fact]
    public void Apply_Total_CountsFilteredNotAllItems()
    {
        PointHealthItem[] items =
        [
            Item("s1", freshness: FreshnessStatus.Stale),
            Item("s2", freshness: FreshnessStatus.Stale),
            Item("f1", freshness: FreshnessStatus.Fresh),
        ];
        var query = Query();
        query.Freshness = [FreshnessStatus.Stale];

        Assert.Equal(2, PointHealthQueryFilter.Apply(items, query).Total);
    }

    [Fact]
    public void Apply_OffsetPastTheEnd_ReturnsEmptyPageButKeepsTotal()
    {
        var items = new[] { Item("PT-1"), Item("PT-2") };
        var query = Query();
        query.Offset = 50;

        var page = PointHealthQueryFilter.Apply(items, query);

        Assert.Empty(page.Items);
        Assert.Equal(2, page.Total);
    }
}
