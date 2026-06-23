using BuildingOs.ApiServer.Protos;
using Grpc.Core;

namespace BuildingOs.ApiServer.Services;

/// <summary>
/// 機器制御の結果を gRPC Server Streaming で通知するサービス。
/// EventBus を購読し、結果が届いたらストリームに書き込む。
/// </summary>
public class PointControlGrpcService : PointControlService.PointControlServiceBase
{
    private readonly IControlResultBus _eventBus;
    private readonly ILogger<PointControlGrpcService> _logger;

    // Online control result wait (#14 sign-off): API → GatewayBridge → gateway → BACnet write →
    // result round-trip. Default 10s; ops can override via CONTROL_RESULT_TIMEOUT_SEC without a rebuild.
    // (Offline gateways already fail fast with 503 via NATS no-responders, #186 — this only bounds the
    // "connected but slow" case.)
    private static readonly TimeSpan Timeout = ResolveTimeout();

    private static TimeSpan ResolveTimeout()
    {
        var raw = Environment.GetEnvironmentVariable("CONTROL_RESULT_TIMEOUT_SEC");
        return int.TryParse(raw, out var sec) && sec > 0
            ? TimeSpan.FromSeconds(sec)
            : TimeSpan.FromSeconds(10);
    }

    public PointControlGrpcService(
        IControlResultBus eventBus,
        ILogger<PointControlGrpcService> logger)
    {
        _eventBus = eventBus;
        _logger = logger;
    }

    public override async Task WaitForResult(
        WaitForResultRequest request,
        IServerStreamWriter<ControlResultEvent> responseStream,
        ServerCallContext context)
    {
        var controlId = request.ControlId;
        _logger.LogInformation("WaitForResult started: controlId={ControlId}", controlId);

        var reader = _eventBus.Subscribe(controlId);

        using var timeoutCts = new CancellationTokenSource(Timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            timeoutCts.Token, context.CancellationToken);

        try
        {
            var evt = await reader.ReadAsync(linkedCts.Token).ConfigureAwait(false);
            await responseStream.WriteAsync(evt).ConfigureAwait(false);

            _logger.LogInformation(
                "WaitForResult completed: controlId={ControlId}, result={Result}",
                controlId, evt.Result);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            _logger.LogWarning("WaitForResult timed out: controlId={ControlId}", controlId);
            throw new RpcException(new Status(StatusCode.DeadlineExceeded, "Control result timed out"));
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("WaitForResult cancelled by client: controlId={ControlId}", controlId);
        }
        finally
        {
            _eventBus.Unsubscribe(controlId);
        }
    }
}
