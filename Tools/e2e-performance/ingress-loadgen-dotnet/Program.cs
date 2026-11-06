// Single-stream GatewayIngress load generator (see IngressLoadGen.csproj).
//
//   dotnet run -c Release -- --target localhost:5051[,localhost:5052,...] --gateway GW-X --points P-0,P-1,... \
//       --rate 5000 --duration 60 [--streams N]
//
// Paces frames by wall clock in 20 ms ticks (no per-frame sleep), one client-stream per --streams
// (default 1); stream i connects to target i % targets (round-robin over ConnectorWorker replicas,
// #626) and sends as gateway "{gateway}-{i}" when N > 1 (seed those gateways' points). Prints one
// JSON line per stream plus a total: sent, accepted (from StreamAck), elapsed seconds and the
// achieved accepted rate. `accepted < sent` only ever means the server
// refused frames (publish_failed / size / unknown point); backpressure shows up as a lower rate.
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using BuildingOS.ConnectorWorker.Protos;
using Grpc.Core;
using Grpc.Net.Client;

var opts = Args.Parse(args);
// One channel (one HTTP/2 connection) per stream, as separate gateways would have.
var startAt = DateTime.UtcNow.AddSeconds(1);
var tasks = Enumerable.Range(0, opts.Streams).Select(i =>
{
    var target = opts.Targets[i % opts.Targets.Length];
    var channel = GrpcChannel.ForAddress($"http://{target}", new GrpcChannelOptions { MaxSendMessageSize = int.MaxValue });
    return RunStreamAsync(new GatewayIngress.GatewayIngressClient(channel), target, opts, i, startAt);
}).ToArray();
var results = await Task.WhenAll(tasks);
foreach (var r in results) Console.WriteLine(JsonSerializer.Serialize(r));
var total = new
{
    stream = "total",
    streams = opts.Streams,
    target_rate = opts.Rate,
    sent = results.Sum(r => r.sent),
    accepted = results.Sum(r => r.accepted),
    elapsed_s = Math.Round(results.Max(r => r.elapsed_s), 3),
    achieved_rate = Math.Round(results.Sum(r => r.accepted) / results.Max(r => r.elapsed_s), 1),
};
Console.WriteLine(JsonSerializer.Serialize(total));
return total.accepted == total.sent ? 0 : 2;

static async Task<StreamResult> RunStreamAsync(GatewayIngress.GatewayIngressClient client, string target, Args opts, int index, DateTime startAt)
{
    var perStreamRate = opts.Rate / opts.Streams + (index < opts.Rate % opts.Streams ? 1 : 0);
    var gateway = opts.Streams == 1 ? opts.Gateway : $"{opts.Gateway}-{index}";
    // --point-prefix gives each stream its own points ("{prefix}-{i}-{j:000}"), so stream i's
    // gateway owns exactly the points it sends (the ingress rejects a gateway/point mismatch).
    var points = opts.PointPrefix is { } prefix
        ? Enumerable.Range(0, opts.PointsPerStream).Select(j => $"{prefix}-{index}-{j:000}").ToArray()
        : opts.Points;
    var totalFrames = (long)perStreamRate * opts.Duration;
    var delay = startAt - DateTime.UtcNow;
    if (delay > TimeSpan.Zero) await Task.Delay(delay);

    using var call = client.StreamTelemetry();
    var sw = Stopwatch.StartNew();
    long sent = 0;
    var t0 = DateTime.UtcNow;
    while (sent < totalFrames)
    {
        var now = DateTime.UtcNow;
        var elapsed = (now - t0).TotalSeconds;
        if (elapsed >= opts.Duration) break;
        var due = Math.Min(totalFrames, (long)(elapsed * perStreamRate));
        if (due <= sent)
        {
            await Task.Delay(20);
            continue;
        }
        for (var k = 0; sent < due; k++, sent++)
        {
            // unique (point, time) per frame: 1 µs apart inside a tick, like the Python harness
            var ts = now.AddTicks(k * 10).ToString("O", CultureInfo.InvariantCulture);
            await call.RequestStream.WriteAsync(new TelemetryFrame
            {
                GatewayId = gateway,
                PointId = points[(int)(sent % points.Length)],
                ValueNum = 20.0 + (sent % 100) / 10.0,
                Timestamp = ts,
            });
        }
    }
    await call.RequestStream.CompleteAsync();
    var ack = await call.ResponseAsync;
    sw.Stop();
    return new StreamResult(index, target, gateway, perStreamRate, sent, ack.Accepted, Math.Round(sw.Elapsed.TotalSeconds, 3),
        Math.Round(ack.Accepted / sw.Elapsed.TotalSeconds, 1));
}

record StreamResult(int stream, string target, string gateway, int target_rate, long sent, long accepted, double elapsed_s, double achieved_rate);

sealed class Args
{
    public string[] Targets = ["localhost:5051"];
    public string Gateway = "GW-LOADGEN";
    public string[] Points = ["P-0"];
    public int Rate = 5000;
    public int Duration = 60;
    public int Streams = 1;
    public string? PointPrefix;
    public int PointsPerStream = 20;

    public static Args Parse(string[] argv)
    {
        var a = new Args();
        for (var i = 0; i < argv.Length; i++)
        {
            string Next() => i + 1 < argv.Length ? argv[++i] : throw new ArgumentException($"{argv[i]} needs a value");
            switch (argv[i])
            {
                case "--target": a.Targets = Next().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); break;
                case "--gateway": a.Gateway = Next(); break;
                case "--points": a.Points = Next().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); break;
                case "--rate": a.Rate = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--duration": a.Duration = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--point-prefix": a.PointPrefix = Next(); break;
                case "--points-per-stream": a.PointsPerStream = Math.Max(1, int.Parse(Next(), CultureInfo.InvariantCulture)); break;
                case "--streams": a.Streams = Math.Max(1, int.Parse(Next(), CultureInfo.InvariantCulture)); break;
                default: throw new ArgumentException($"unknown argument {argv[i]}");
            }
        }
        return a;
    }
}
