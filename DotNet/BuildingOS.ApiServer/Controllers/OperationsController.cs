using BuildingOs.ApiServer.Filters;
using BuildingOS.Shared.Infrastructure.Monitoring;
using Microsoft.AspNetCore.Mvc;

namespace BuildingOs.ApiServer.Controllers;

/// <summary>
/// データ流量（Platform 由来、Prometheus 集計）の応答（#451 Phase 1）。Point 母数 / Fresh 率は
/// <c>telemetry/health/summary</c>（#452, domain 側）の責務で、ここには含めない。
/// </summary>
public sealed record OperationsSummaryResponse(
    double? MsgRate1m,
    double? MsgRate1hAvg,
    bool MetricsAvailable);

/// <summary>
/// `/home` が必要とする Platform 由来（Prometheus 集計）の KPI だけを公開する。全ロールが見る画面
/// なので admin 専用にはしない（集計値のみで Point/建物の情報を含まない）。
/// </summary>
[ApiController]
[Route("api/operations")]
[AuthorizeFilter]
public class OperationsController : ControllerBase
{
    /// <summary>過去 1 時間の平均。<see cref="SystemStatusService.MsgRate1mQuery"/> と同じ 1 分刻みの
    /// recording rule (<c>connector:messages_processed:rate1m</c>, `oss-stack/prometheus/recording_rules.yml`
    /// の eval interval と一致) を、その解像度でサブクエリして平均する。</summary>
    public const string MsgRate1hAvgQuery =
        $"avg_over_time(({SystemStatusService.MsgRate1mQuery})[1h:1m])";

    private readonly IPrometheusQueryClient _prometheus;

    public OperationsController(IPrometheusQueryClient prometheus)
    {
        _prometheus = prometheus;
    }

    /// <summary>現在のデータ流量（1m 平均）と過去 1 時間平均。Prometheus 未配線ならどちらも null
    /// （エラーにしない — カードは非表示にできる）。</summary>
    [HttpGet("summary")]
    [ProducesResponseType(typeof(OperationsSummaryResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<OperationsSummaryResponse>> Summary(CancellationToken ct)
    {
        var rate1mTask = _prometheus.QueryScalarAsync(SystemStatusService.MsgRate1mQuery, ct);
        var rate1hAvgTask = _prometheus.QueryScalarAsync(MsgRate1hAvgQuery, ct);
        await Task.WhenAll(rate1mTask, rate1hAvgTask).ConfigureAwait(false);

        return Ok(new OperationsSummaryResponse(
            MsgRate1m: await rate1mTask.ConfigureAwait(false),
            MsgRate1hAvg: await rate1hAvgTask.ConfigureAwait(false),
            MetricsAvailable: _prometheus.IsConfigured));
    }
}
