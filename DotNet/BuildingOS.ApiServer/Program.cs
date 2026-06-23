using BuildingOS.Shared.Infrastructure.Telemetry;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace BuildingOs.ApiServer;

public class Program
{
    public static void Main(string[] args)
    {
        var port = int.Parse(Environment.GetEnvironmentVariable("PORT") ?? "8080");

        // OTLP log export (no-op when OTEL_EXPORTER_OTLP_ENDPOINT is unset). Tracing and
        // metrics are wired in Startup.ConfigureServices via AddOtlpTelemetry.
        var otlpEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
        var otelServiceName = Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME") ?? "building-os-api";

        var host = Host
            .CreateDefaultBuilder(args)
            .ConfigureLogging(logging => logging.AddOtlpLogging(otelServiceName, otlpEndpoint))
            .ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.UseStartup<Startup>();
                webBuilder.ConfigureKestrel(options =>
                {
                    options.ListenAnyIP(port, listenOptions =>
                    {
                        listenOptions.Protocols = HttpProtocols.Http1AndHttp2;
                    });
                });
            })
            .Build();

        host.Run();
    }
}
