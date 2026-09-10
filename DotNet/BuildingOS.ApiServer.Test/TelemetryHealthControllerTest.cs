using BuildingOs.ApiServer.Authorization;
using BuildingOs.ApiServer.Controllers;
using BuildingOS.Shared;
using BuildingOS.Shared.Domain.Authorization;
using BuildingOS.Shared.Domain.Configuration;
using BuildingOS.Shared.Domain.Health;
using BuildingOS.Shared.Infrastructure.Oss;
using BuildingOS.Shared.Infrastructure.Telemetry;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace BuildingOS.ApiServer.Test;

/// <summary>
/// データ健全性エンドポイント（#452, <c>GET /api/telemetry/health</c> と <c>/summary</c>）の仕様。
///
/// <para>このコントローラは <b>台帳（認可済み） × 最終受信インデックス × 閾値設定 × gateway 接続状態</b>
/// を突き合わせて 3 軸の判定済み行を作るだけで、判定そのものは <c>PointHealthClassifier</c>、
/// 絞り込み・並べ替えは <c>PointHealthQueryFilter</c> に置く。ここで検証するのは
/// <b>組み立て方</b>（認可の範囲、index 状態の伝播、gateway 問い合わせの回数、集計）であって
/// 判定の境界値ではない（それは <c>PointHealthClassifierTest</c> の担当）。</para>
///
/// <para>新しいデータストアは足さない。最新値は <see cref="IPointLastSeenIndex"/> の 1 回の
/// メモリ参照で引き、<c>IHotTelemetryStore</c> を Point 件数ぶん叩くことはしない。</para>
/// </summary>
public class TelemetryHealthControllerTest
{
    private static readonly DateTimeOffset Now = new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);

    // -------------------------------------------------------------------------
    // テストダブル
    // -------------------------------------------------------------------------

    /// <summary>固定時刻。齢の計算を決定的にするためだけのもの。</summary>
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>
    /// index のテストダブル。out 引数があるので Moq より手書きの方が読みやすい。
    /// </summary>
    private sealed class FakeIndex : IPointLastSeenIndex
    {
        private readonly Dictionary<string, PointLastSeenEntry> _entries = new(StringComparer.Ordinal);

        public PointLastSeenIndexState State { get; set; } = PointLastSeenIndexState.Ready;
        public DateTimeOffset? LastSyncAt { get; set; }
        public int Count => _entries.Count;

        public void Set(string pointId, DateTimeOffset? lastSeen, double? value = 23.4) =>
            _entries[pointId] = new PointLastSeenEntry(pointId, lastSeen, value, "number");

        public bool TryGet(string pointId, out PointLastSeenEntry entry)
        {
            if (_entries.TryGetValue(pointId, out var found))
            {
                entry = found;
                return true;
            }

            entry = null!;
            return false;
        }
    }

    private sealed record Harness(
        TelemetryHealthController Controller,
        Mock<IAuthorizedTwinView> View,
        FakeIndex Index,
        Mock<IGatewayConnectionStatusStore> Gateways);

    private static Harness Build(
        Dictionary<string, PointDetail[]> byBuilding,
        PointLastSeenIndexState indexState = PointLastSeenIndexState.Ready)
    {
        var view = new Mock<IAuthorizedTwinView>();
        view.Setup(v => v.ListBuildingsAsync(It.IsAny<AuthorizationContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(byBuilding.Keys
                .Select(dtId => new Building { DtId = dtId, Id = dtId, Name = dtId })
                .ToArray());
        view.Setup(v => v.ListPointDetailsAsync(
                It.IsAny<AuthorizationContext>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AuthorizationContext _, string dtId, CancellationToken _) =>
                byBuilding.TryGetValue(dtId, out var points) ? points : Array.Empty<PointDetail>());

        var settings = new Mock<ISystemSettingsService>();
        settings.Setup(s => s.GetTelemetryThresholdsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TelemetryThresholds(300, 3));

        // 既定は「接続エントリ無し＝未接続」。接続済みを見たいテストだけ上書きする。
        var gateways = new Mock<IGatewayConnectionStatusStore>();
        gateways.Setup(g => g.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((GatewayConnectionStatus?)null);

        var index = new FakeIndex { State = indexState };

        var controller = new TelemetryHealthController(
            view.Object, index, settings.Object, gateways.Object, new FixedClock(Now))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    Items =
                    {
                        ["AuthorizationContext"] =
                            new AuthorizationContext { UserId = "u1", Role = "operator", Permissions = [] },
                    },
                },
            },
        };

        return new Harness(controller, view, index, gateways);
    }

    private static Harness Build(params PointDetail[] points) =>
        Build(new Dictionary<string, PointDetail[]> { ["urn:dtid:b1"] = points });

    private static void Connected(Harness h, string gatewayId) =>
        h.Gateways.Setup(g => g.GetAsync(gatewayId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GatewayConnectionStatus("replica-1", Now));

    private static PointDetail Detail(
        string pointId,
        string name = "給気温度",
        string? gatewayId = null,
        float? interval = null,
        float? alarmHigh = null,
        string[]? tags = null,
        string deviceDtId = "urn:dtid:d1",
        string deviceName = "AHU-01") => new()
        {
            Point = new Point
            {
                DtId = "urn:dtid:" + pointId,
                Id = pointId,
                Name = name,
                Unit = "Cel",
                Interval = interval,
                GatewayName = gatewayId,
                AlarmHigh = alarmHigh,
                CustomTags = (tags ?? Array.Empty<string>()).ToDictionary(t => t, _ => true),
            },
            Device = new Device
            {
                DtId = deviceDtId,
                Id = "AHU-01",
                Name = deviceName,
                BuildingName = "本館",
            },
            Floor = new Floor { DtId = "urn:dtid:f1", Id = "1F", Name = "1F" },
            Space = new Space { DtId = "urn:dtid:s1", Id = "R101", Name = "会議室A" },
        };

    private static Task<ActionResult<PointHealthListResponse>> Get(
        Harness h,
        string? buildingDtId = null,
        string? floorDtId = null,
        string? deviceDtId = null,
        string? gatewayId = null,
        string[]? freshness = null,
        string[]? alarm = null,
        string[]? healthStatus = null,
        int? olderThan = null,
        string[]? tag = null,
        string? q = null,
        string? sort = null,
        int limit = 100,
        int offset = 0) =>
        h.Controller.Get(
            buildingDtId, floorDtId, deviceDtId, gatewayId,
            freshness, alarm, healthStatus, olderThan, tag, q, sort, limit, offset, default);

    private static PointHealthListResponse Body(ActionResult<PointHealthListResponse> result) =>
        Assert.IsType<PointHealthListResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);

    private static PointHealthSummaryResponse Body(ActionResult<PointHealthSummaryResponse> result) =>
        Assert.IsType<PointHealthSummaryResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);

    // -------------------------------------------------------------------------
    // 認可の範囲
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Get_NoBuildingScope_OnlyReadsAuthorizedBuildings()
    {
        // 認可済み建物の列挙は IAuthorizedTwinView に委ねる。ここに映るのは b1 だけで、
        // 権限の無い b2 は「そもそも問い合わせない」（存在も漏らさない）。
        var h = Build(new Dictionary<string, PointDetail[]>
        {
            ["urn:dtid:b1"] = [Detail("PT-1")],
        });
        h.Index.Set("PT-1", Now.AddSeconds(-10));

        var body = Body(await Get(h));

        Assert.Equal("PT-1", Assert.Single(body.Items).PointId);
        Assert.Equal("urn:dtid:b1", body.Items[0].BuildingDtId);
        h.View.Verify(v => v.ListPointDetailsAsync(
            It.IsAny<AuthorizationContext>(), "urn:dtid:b1", It.IsAny<CancellationToken>()), Times.Once());
        h.View.Verify(v => v.ListPointDetailsAsync(
            It.IsAny<AuthorizationContext>(), "urn:dtid:b2", It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public async Task Get_WithBuildingScope_ReadsOnlyThatBuilding()
    {
        var h = Build(new Dictionary<string, PointDetail[]>
        {
            ["urn:dtid:b1"] = [Detail("PT-1")],
            ["urn:dtid:b2"] = [Detail("PT-2")],
        });
        h.Index.Set("PT-1", Now.AddSeconds(-10));
        h.Index.Set("PT-2", Now.AddSeconds(-10));

        var body = Body(await Get(h, buildingDtId: "urn:dtid:b2"));

        Assert.Equal("PT-2", Assert.Single(body.Items).PointId);
        h.View.Verify(v => v.ListBuildingsAsync(
            It.IsAny<AuthorizationContext>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    // -------------------------------------------------------------------------
    // 入力の検証
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Get_NegativeOffset_Returns400()
    {
        var h = Build(Detail("PT-1"));

        var result = await Get(h, offset: -1);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Theory]
    [InlineData(999, 500)]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(50, 50)]
    public async Task Get_ClampsLimitIntoRange(int requested, int expected)
    {
        var h = Build(Detail("PT-1"));

        var body = Body(await Get(h, limit: requested));

        Assert.Equal(expected, body.Limit);
    }

    [Fact]
    public async Task Get_EchoesOffset()
    {
        var h = Build(Detail("PT-1"));

        var body = Body(await Get(h, offset: 0));

        Assert.Equal(0, body.Offset);
    }

    // -------------------------------------------------------------------------
    // index の状態（false missing を出さない）
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Get_IndexWarming_ReportsIncompleteDataAndUnknownPoints()
    {
        // 起動直後にエントリが無いだけで「全 Point 欠測」と言ってはならない。
        var h = Build(new Dictionary<string, PointDetail[]> { ["urn:dtid:b1"] = [Detail("PT-1")] },
            indexState: PointLastSeenIndexState.Warming);

        var body = Body(await Get(h));

        Assert.False(body.DataComplete);
        Assert.Equal(PointLastSeenIndexState.Warming, body.IndexState);
        var item = Assert.Single(body.Items);
        Assert.Equal(FreshnessStatus.Unknown, item.Freshness.Status);
        Assert.Equal(HealthStatus.Unknown, item.HealthStatus);
        Assert.Null(item.Freshness.Reason);
    }

    [Fact]
    public async Task Get_IndexDegraded_StillReturnsRowsButFlagsIncompleteData()
    {
        // watch が切れていても手元の値は返す。落とすのは dataComplete だけ。
        var h = Build(new Dictionary<string, PointDetail[]> { ["urn:dtid:b1"] = [Detail("PT-1")] },
            indexState: PointLastSeenIndexState.Degraded);
        h.Index.Set("PT-1", Now.AddSeconds(-10));

        var body = Body(await Get(h));

        Assert.False(body.DataComplete);
        Assert.Equal(PointLastSeenIndexState.Degraded, body.IndexState);
        Assert.Equal(FreshnessStatus.Fresh, Assert.Single(body.Items).Freshness.Status);
    }

    [Fact]
    public async Task Get_IndexReady_ReportsCompleteData()
    {
        var h = Build(Detail("PT-1"));
        h.Index.Set("PT-1", Now.AddSeconds(-10));

        var body = Body(await Get(h));

        Assert.True(body.DataComplete);
        Assert.Equal(PointLastSeenIndexState.Ready, body.IndexState);
    }

    [Fact]
    public async Task Get_IndexReadyAndNoEntry_IsMissingNeverReceived()
    {
        var h = Build(Detail("PT-1", gatewayId: "GW-001"));
        Connected(h, "GW-001");

        var item = Assert.Single(Body(await Get(h)).Items);

        Assert.Equal(FreshnessStatus.Missing, item.Freshness.Status);
        Assert.Equal(MissingReason.NeverReceived, item.Freshness.Reason);
        Assert.Equal(HealthStatus.Missing, item.HealthStatus);
    }

    // -------------------------------------------------------------------------
    // gateway 接続状態
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Get_MissingPointOnDisconnectedGateway_ExplainsGatewayDisconnected()
    {
        // 「一度も来ていない」より「gateway が落ちている」の方が運用者の次の一手に直結する。
        var h = Build(Detail("PT-1", gatewayId: "GW-001"));

        var item = Assert.Single(Body(await Get(h)).Items);

        Assert.NotNull(item.Gateway);
        Assert.Equal("GW-001", item.Gateway!.Id);
        Assert.False(item.Gateway.Connected);
        Assert.Equal(MissingReason.GatewayDisconnected, item.Freshness.Reason);
    }

    [Fact]
    public async Task Get_QueriesEachGatewayStatusOnlyOncePerRequest()
    {
        // 数千 Point ぶん KV を叩かない。gateway ごとに 1 回だけ引いて使い回す。
        var h = Build(new Dictionary<string, PointDetail[]>
        {
            ["urn:dtid:b1"] =
            [
                Detail("PT-1", gatewayId: "GW-001"),
                Detail("PT-2", gatewayId: "GW-001"),
                Detail("PT-3", gatewayId: "GW-001"),
                Detail("PT-4", gatewayId: "GW-002"),
            ],
        });
        Connected(h, "GW-001");

        await Get(h);

        h.Gateways.Verify(g => g.GetAsync("GW-001", It.IsAny<CancellationToken>()), Times.Once());
        h.Gateways.Verify(g => g.GetAsync("GW-002", It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task Get_PointWithoutGateway_HasNoGatewayBlockAndIsNotQueried()
    {
        var h = Build(Detail("PT-1"));
        h.Index.Set("PT-1", Now.AddSeconds(-10));

        var item = Assert.Single(Body(await Get(h)).Items);

        Assert.Null(item.Gateway);
        h.Gateways.Verify(g => g.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public async Task Get_GatewayId_FallsBackToTheDeviceWhenThePointHasNone()
    {
        var detail = Detail("PT-1");
        detail.Device!.GatewayId = "GW-DEV";
        var h = Build(detail);
        Connected(h, "GW-DEV");
        h.Index.Set("PT-1", Now.AddSeconds(-10));

        var item = Assert.Single(Body(await Get(h)).Items);

        Assert.Equal("GW-DEV", item.Gateway?.Id);
        Assert.True(item.Gateway?.Connected);
    }

    // -------------------------------------------------------------------------
    // 台帳 × index の突き合わせ
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Get_ProjectsPointMetadataAndFreshnessFromTheTwinAndIndex()
    {
        var h = Build(Detail("SAT-001", name: "給気温度", gatewayId: "GW-001", interval: 300, tags: ["critical"]));
        Connected(h, "GW-001");
        h.Index.Set("SAT-001", Now.AddSeconds(-1080), value: 23.4);

        var item = Assert.Single(Body(await Get(h)).Items);

        Assert.Equal("SAT-001", item.PointId);
        Assert.Equal("urn:dtid:SAT-001", item.PointDtId);
        Assert.Equal("給気温度", item.Name);
        Assert.Equal("Cel", item.Unit);
        Assert.Equal(FreshnessStatus.Stale, item.Freshness.Status);
        Assert.Equal(1080, item.Freshness.AgeSeconds);
        Assert.Equal(300, item.Freshness.ExpectedIntervalSeconds);
        Assert.Equal(900, item.Freshness.ThresholdSeconds);
        Assert.Equal(ThresholdSource.Point, item.Freshness.ThresholdSource);
        Assert.Equal(Now.AddSeconds(-1080), item.Freshness.LastSeen);
        Assert.Equal(23.4, item.Alarm.Value);
        Assert.Equal(AlarmStatus.Suppressed, item.Alarm.Status);
        Assert.Equal(HealthStatus.Stale, item.HealthStatus);
        Assert.Equal("urn:dtid:d1", item.DeviceDtId);
        Assert.Equal("AHU-01", item.DeviceName);
        Assert.Equal("urn:dtid:s1", item.SpaceDtId);
        Assert.Equal("会議室A", item.SpaceName);
        Assert.Equal("urn:dtid:f1", item.FloorDtId);
        Assert.Equal("1F", item.FloorName);
        Assert.Equal("urn:dtid:b1", item.BuildingDtId);
        Assert.Equal("本館", item.BuildingName);
        Assert.Equal(["critical"], item.Tags);
    }

    [Fact]
    public async Task Get_NoExpectedInterval_UsesTheSystemDefaultThreshold()
    {
        var h = Build(Detail("PT-1"));
        h.Index.Set("PT-1", Now.AddSeconds(-400));

        var item = Assert.Single(Body(await Get(h)).Items);

        Assert.Equal(ThresholdSource.System, item.Freshness.ThresholdSource);
        Assert.Equal(300, item.Freshness.ThresholdSeconds);
        Assert.Equal(FreshnessStatus.Stale, item.Freshness.Status);
    }

    [Fact]
    public async Task Get_FreshPointOverAlarmHigh_KeepsBothAxes()
    {
        var h = Build(Detail("PT-1", interval: 300, alarmHigh: 30));
        h.Index.Set("PT-1", Now.AddSeconds(-10), value: 35);

        var item = Assert.Single(Body(await Get(h)).Items);

        Assert.Equal(FreshnessStatus.Fresh, item.Freshness.Status);
        Assert.Equal(AlarmStatus.Critical, item.Alarm.Status);
        Assert.Equal(AlarmBound.AlarmHigh, item.Alarm.Violated);
        Assert.Equal(HealthStatus.Critical, item.HealthStatus);
    }

    // -------------------------------------------------------------------------
    // 絞り込みクエリの受け渡し
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Get_FreshnessQuery_IsParsedCaseInsensitivelyAndApplied()
    {
        var h = Build(new Dictionary<string, PointDetail[]>
        {
            ["urn:dtid:b1"] = [Detail("PT-fresh"), Detail("PT-stale")],
        });
        h.Index.Set("PT-fresh", Now.AddSeconds(-10));
        h.Index.Set("PT-stale", Now.AddSeconds(-4000));

        var body = Body(await Get(h, freshness: ["Stale"]));

        Assert.Equal("PT-stale", Assert.Single(body.Items).PointId);
        Assert.Equal(1, body.Total);
    }

    [Fact]
    public async Task Get_UnknownFilterValue_IsIgnoredRatherThanFailing()
    {
        // 未知の値で 400 にすると、フロントの表記ゆれで画面が丸ごと落ちる。無視して素通し。
        var h = Build(Detail("PT-1"));
        h.Index.Set("PT-1", Now.AddSeconds(-10));

        var body = Body(await Get(h, freshness: ["bogus"]));

        Assert.Equal(1, body.Total);
    }

    [Fact]
    public async Task Get_OlderThanQuery_IncludesPointsThatNeverReported()
    {
        var h = Build(new Dictionary<string, PointDetail[]>
        {
            ["urn:dtid:b1"] = [Detail("PT-never"), Detail("PT-recent")],
        });
        h.Index.Set("PT-recent", Now.AddSeconds(-10));

        var body = Body(await Get(h, olderThan: 900));

        Assert.Equal("PT-never", Assert.Single(body.Items).PointId);
    }

    [Fact]
    public async Task Get_Paging_ReportsTotalBeforePaging()
    {
        var points = Enumerable.Range(1, 5).Select(i => Detail($"PT-{i}")).ToArray();
        var h = Build(new Dictionary<string, PointDetail[]> { ["urn:dtid:b1"] = points });

        var body = Body(await Get(h, limit: 2, offset: 0));

        Assert.Equal(2, body.Items.Count);
        Assert.Equal(5, body.Total);
        Assert.Equal(2, body.Limit);
    }

    // -------------------------------------------------------------------------
    // /summary
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Summary_CountsEachAxisIndependently()
    {
        var h = Build(new Dictionary<string, PointDetail[]>
        {
            ["urn:dtid:b1"] =
            [
                Detail("PT-fresh"),
                Detail("PT-warn", alarmHigh: 30),
                Detail("PT-critical", alarmHigh: 30),
                Detail("PT-stale"),
                Detail("PT-missing"),
            ],
        });
        h.Index.Set("PT-fresh", Now.AddSeconds(-10), value: 1);
        h.Index.Set("PT-warn", Now.AddSeconds(-10), value: 1);
        h.Index.Set("PT-critical", Now.AddSeconds(-10), value: 35);
        h.Index.Set("PT-stale", Now.AddSeconds(-4000), value: 1);

        var body = Body(await h.Controller.Summary(
            buildingDtId: null, floorDtId: null, deviceDtId: null, gatewayId: null,
            tag: null, q: null, ct: default));

        Assert.Equal(5, body.TotalPoints);
        Assert.Equal(3, body.Fresh);
        Assert.Equal(1, body.Stale);
        Assert.Equal(1, body.Missing);
        Assert.Equal(0, body.Unknown);
        Assert.Equal(1, body.AlarmCritical);
        Assert.True(body.DataComplete);
        Assert.Equal(PointLastSeenIndexState.Ready, body.IndexState);
    }

    [Fact]
    public async Task Summary_SuppressedAlarmsAreNotCountedAsAlarms()
    {
        // 届いていない値で警報を数えると、gateway 断のたびに警報件数が跳ね上がる。
        var h = Build(Detail("PT-1", alarmHigh: 30));
        h.Index.Set("PT-1", Now.AddSeconds(-4000), value: 99);

        var body = Body(await h.Controller.Summary(
            buildingDtId: null, floorDtId: null, deviceDtId: null, gatewayId: null,
            tag: null, q: null, ct: default));

        Assert.Equal(1, body.Stale);
        Assert.Equal(0, body.AlarmCritical);
        Assert.Equal(0, body.AlarmWarn);
    }

    [Fact]
    public async Task Summary_IndexWarming_CountsUnknownAndFlagsIncompleteData()
    {
        var h = Build(new Dictionary<string, PointDetail[]> { ["urn:dtid:b1"] = [Detail("PT-1")] },
            indexState: PointLastSeenIndexState.Warming);

        var body = Body(await h.Controller.Summary(
            buildingDtId: null, floorDtId: null, deviceDtId: null, gatewayId: null,
            tag: null, q: null, ct: default));

        Assert.Equal(1, body.TotalPoints);
        Assert.Equal(1, body.Unknown);
        Assert.Equal(0, body.Missing);
        Assert.False(body.DataComplete);
        Assert.Equal(PointLastSeenIndexState.Warming, body.IndexState);
    }

    [Fact]
    public async Task Summary_ScopeFilter_NarrowsTheAggregate()
    {
        var h = Build(new Dictionary<string, PointDetail[]>
        {
            ["urn:dtid:b1"] = [Detail("PT-1"), Detail("PT-2")],
            ["urn:dtid:b2"] = [Detail("PT-3")],
        });

        var body = Body(await h.Controller.Summary(
            buildingDtId: "urn:dtid:b2", floorDtId: null, deviceDtId: null, gatewayId: null,
            tag: null, q: null, ct: default));

        Assert.Equal(1, body.TotalPoints);
    }
}
