using BuildingOs.ApiServer.Modules;
using BuildingOS.Shared;
using BuildingOS.Shared.Infrastructure;
using BuildingOS.Shared.Infrastructure.AdminAudit;
using BuildingOS.Shared.Infrastructure.Assistant;
using BuildingOS.Shared.Infrastructure.BlobStorage;
using BuildingOS.Shared.Infrastructure.Configuration;
using BuildingOS.Shared.Infrastructure.ControlRouting;
using BuildingOS.Shared.Infrastructure.OxiGraph;
using BuildingOS.Shared.Infrastructure.Monitoring;
using BuildingOS.Shared.Infrastructure.Oss;
using BuildingOS.Shared.Infrastructure.PointControlAudit;
using BuildingOS.Shared.Infrastructure.Telemetry;
using BuildingOS.Shared.Infrastructure.Telemetry.ParquetLake;
using BuildingOS.Shared.Domain.AdminAudit;
using BuildingOS.Shared.Domain.Assistant;
using BuildingOS.Shared.Domain.Authorization;
using BuildingOS.Shared.Domain.Configuration;
using BuildingOS.Shared.Domain.Grouping;
using BuildingOS.Shared.Domain.UserManagement;
using BuildingOs.ApiServer.Middlewares;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using System.Text.Json;
using BuildingOs.Shared;
using NATS.Client.JetStream;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using System.Globalization;

namespace BuildingOs.ApiServer
{
    public class Startup
    {
        private const string HealthPath = "/health";

        private EnvModule _envModule = null!;
        public IConfiguration Configuration { get; }

        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public void ConfigureServices(IServiceCollection services)
        {
            _envModule = new EnvModule();

            // === OxiGraph (digital twin + hierarchy resolver) ===
            services.AddHttpClient();
            services.AddSingleton(sp =>
            {
                var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("oxigraph");
                return new BuildingOS.Shared.Infrastructure.OxiGraph.OxiGraphClient(http, _envModule.OxiGraphEndpoint);
            });

            // Seed OxiGraph with pointlist RDF on startup (no-op when OXIGRAPH_SEED_TTL_PATH is unset)
            services.AddHostedService<OxiGraphSeedHostedService>();

            // === Digital twin layer ===
            services.AddScoped<IDigitalTwinDatabase, OxiGraphDigitalTwinDatabase>();
            services.AddScoped<IControlSchemaResolver, OssControlSchemaResolver>();
            // Twin admin tools (#322): staged import preview/apply + read-only SPARQL console.
            services.AddScoped<BuildingOS.Shared.Domain.TwinAdmin.ITwinAdminService>(
                sp => new OxiGraphTwinAdminService(
                    sp.GetRequiredService<OxiGraphClient>(),
                    sp.GetRequiredService<ILogger<OxiGraphTwinAdminService>>()));

            // === Telemetry layer ===
            services.AddSingleton<INatsJSContext>(sp =>
            {
                var conn = sp.GetRequiredService<NATS.Client.Core.INatsConnection>();
                return new NatsJSContext(conn);
            });
            services.AddSingleton<IHotTelemetryStore, NatsKvLatestStore>();

            // Warm-tier mode (#216): parquet (default) reads the unified Parquet lake for warm+cold+agg;
            // timescale opts back into the TimescaleDB warm/aggregate stores (+ the MinIO cold reader).
            var parquetMode = WarmStoreMode.IsParquet(_envModule.WarmStore);

            // MinIO/S3 — the lake's object store. In parquet mode it backs the unified lake store; in
            // timescale mode it is only the cold-tier reader (#212). Registered when MinIO is configured.
            if (!string.IsNullOrEmpty(_envModule.MinioEndpoint))
            {
                services.AddSingleton<Amazon.S3.IAmazonS3>(_ =>
                {
                    var s3Config = new Amazon.S3.AmazonS3Config
                    {
                        ServiceURL = _envModule.MinioEndpoint,
                        ForcePathStyle = true,
                    };
                    return new Amazon.S3.AmazonS3Client(
                        new Amazon.Runtime.BasicAWSCredentials(_envModule.MinioAccessKey, _envModule.MinioSecretKey),
                        s3Config);
                });
                services.AddSingleton<IBlobStorage>(sp =>
                    new MinioBlobStorage(sp.GetRequiredService<Amazon.S3.IAmazonS3>()));

                if (parquetMode)
                {
                    // One lake reader serves warm + cold + multi-point (injected below as both tiers).
                    services.AddSingleton<ParquetLakeTelemetryStore>(sp =>
                        new ParquetLakeTelemetryStore(
                            sp.GetRequiredService<IBlobStorage>(),
                            sp.GetRequiredService<IMemoryCache>(),
                            new ParquetLakeTelemetryStoreOptions
                            {
                                LatestLookbackHours = _envModule.ParquetLatestLookbackHours,
                                QueryMaxFiles = _envModule.ParquetQueryMaxFiles,
                            },
                            sp.GetRequiredService<ILogger<ParquetLakeTelemetryStore>>()));

                    // Tail-merged warm store registered as Singleton so both ITelemetryDatabase and
                    // ITelemetryQueryRouter share the same instance (avoid two NATS consumers per scope).
                    if (_envModule.TailMergeEnabled)
                    {
                        services.AddSingleton<TailMergedTelemetryStore>(sp =>
                            new TailMergedTelemetryStore(
                                sp.GetRequiredService<ParquetLakeTelemetryStore>(),
                                new NatsTailReader(sp.GetRequiredService<INatsJSContext>()),
                                new TailMergeOptions
                                {
                                    LookbackSec  = _envModule.TailMergeLookbackSec,
                                    MaxMsgs      = _envModule.TailMergeMaxMsgs,
                                    FetchTimeout = TimeSpan.FromMilliseconds(_envModule.TailMergeTimeoutMs),
                                },
                                sp.GetRequiredService<ILogger<TailMergedTelemetryStore>>()));
                    }
                }
                else
                {
                    services.AddSingleton<IColdTelemetryStore>(sp =>
                        new MinioParquetColdTelemetryStore(
                            sp.GetRequiredService<IBlobStorage>(),
                            sp.GetRequiredService<IMemoryCache>()));
                }
            }

            services.AddScoped<ITelemetryDatabase>(sp =>
            {
                var log = sp.GetRequiredService<ILogger<OssTelemetryDatabase>>();
                var hot = sp.GetRequiredService<IHotTelemetryStore>();

                if (parquetMode)
                {
                    var lake = sp.GetService<ParquetLakeTelemetryStore>();
                    IWarmTelemetryStore? lakeWarm =
                        sp.GetService<TailMergedTelemetryStore>() as IWarmTelemetryStore ?? lake;
                    return new OssTelemetryDatabase(log, warm: lakeWarm, hot: hot, cold: lake);
                }

                IWarmTelemetryStore? warm = !string.IsNullOrEmpty(_envModule.TimescaleConnectionString)
                    ? new NpgsqlWarmTelemetryStore(_envModule.TimescaleConnectionString)
                    : null;
                var cold = sp.GetService<IColdTelemetryStore>();
                return new OssTelemetryDatabase(log, warm, hot, cold);
            });
            services.AddScoped<ITelemetryQueryRouter>(sp =>
            {
                var log = sp.GetRequiredService<ILogger<OssTelemetryQueryRouter>>();
                var cache = sp.GetRequiredService<IMemoryCache>();
                var hot = sp.GetRequiredService<IHotTelemetryStore>();

                if (parquetMode)
                {
                    var lake = sp.GetService<ParquetLakeTelemetryStore>();
                    IWarmTelemetryStore? lakeWarm =
                        sp.GetService<TailMergedTelemetryStore>() as IWarmTelemetryStore ?? lake;
                    IAggregatedTelemetryStore? lakeAgg = null;
                    if (lake is not null)
                    {
                        var blob    = sp.GetRequiredService<IBlobStorage>();
                        var cache2  = sp.GetRequiredService<IMemoryCache>();
                        var aggLog  = sp.GetRequiredService<ILogger<RollupParquetTelemetryStore>>();
                        var aggFallback = new AggregatingParquetTelemetryStore(lake);
                        lakeAgg = new RollupParquetTelemetryStore(blob, cache2, aggFallback, aggLog);
                    }
                    return new OssTelemetryQueryRouter(log, cache, hot, warm: lakeWarm, cold: lake, agg: lakeAgg);
                }

                IWarmTelemetryStore? warm = !string.IsNullOrEmpty(_envModule.TimescaleConnectionString)
                    ? new NpgsqlWarmTelemetryStore(_envModule.TimescaleConnectionString)
                    : null;
                IAggregatedTelemetryStore? agg = !string.IsNullOrEmpty(_envModule.TimescaleConnectionString)
                    ? new NpgsqlAggregatedTelemetryStore(_envModule.TimescaleConnectionString)
                    : null;
                var cold = sp.GetService<IColdTelemetryStore>();
                return new OssTelemetryQueryRouter(log, cache, hot, warm, cold: cold, agg: agg);
            });

            // === Point control layer ===
            services.AddScoped<IPointControlRepository, EfPointControlRepository>();

            // === RelationalDbContext (user/group/authorization on shared OSS Postgres) ===
            if (string.IsNullOrEmpty(_envModule.PostgresConnectionString)
                && _envModule.AspNetCoreEnvironment != "Development")
            {
                throw new InvalidOperationException(
                    "POSTGRES_CONNECTION_STRING is required outside the Development environment.");
            }

            services.AddDbContext<RelationalDbContext>(options =>
                options.UseNpgsql(_envModule.PostgresConnectionString));
            services.AddScoped<IGroupRepository, GroupRepository>();
            services.AddScoped<ISystemConfigStore, SystemConfigStore>();
            services.AddScoped<ISystemSettingsService, SystemSettingsService>();
            services.AddScoped<IAdminAuditRecorder, EfAdminAuditRecorder>();

            // === User management (Keycloak Admin API) ===
            services.AddUserManagementService(_envModule);

            // === OIDC client management (Keycloak Admin API; 503 when unconfigured) ===
            services.AddOidcClientManagementService(_envModule);

            // === Simple monitoring (system/status) ===
            // Service up/down comes from a /health fan-out (Prometheus-independent); only KPIs use
            // Prometheus and degrade to null when unset. Short timeouts keep the endpoint snappy:
            // a hung dependency degrades to "down"/null fast instead of blocking the request.
            services.AddHttpClient("prometheus", c => c.Timeout = TimeSpan.FromSeconds(3));
            services.AddHttpClient("health-probe", c => c.Timeout = TimeSpan.FromSeconds(2));
            services.AddSingleton<IPrometheusQueryClient>(sp =>
            {
                var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("prometheus");
                var logger = sp.GetRequiredService<ILogger<PrometheusQueryClient>>();
                return new PrometheusQueryClient(http, _envModule.PrometheusUrl, logger);
            });
            services.AddSingleton<IServiceHealthProbe>(sp =>
            {
                var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("health-probe");
                var logger = sp.GetRequiredService<ILogger<HttpServiceHealthProbe>>();
                var targets = ServiceHealthTarget.ParseList(_envModule.SystemStatusHealthTargets);
                return new HttpServiceHealthProbe(http, targets, logger);
            });
            services.AddSingleton<ISystemStatusService, SystemStatusService>();
            services.AddSingleton<IEffectiveConfigService, EffectiveConfigService>();

            // === Experimental/optional help assistant (#151) ===
            // Disabled unless ASSISTANT_LLM_URL points at an OpenAI-compatible base (e.g. the local
            // Ollama optional profile). AssistantService reports disabled (503) when no client is wired.
            if (!string.IsNullOrWhiteSpace(_envModule.AssistantLlmUrl))
            {
                var baseUrl = _envModule.AssistantLlmUrl.EndsWith('/')
                    ? _envModule.AssistantLlmUrl
                    : _envModule.AssistantLlmUrl + "/";
                services.AddHttpClient("assistant-llm", client =>
                {
                    client.BaseAddress = new Uri(baseUrl);
                    client.Timeout = TimeSpan.FromSeconds(60);
                });
                services.AddScoped<IAssistantLlmClient>(sp =>
                {
                    var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("assistant-llm");
                    return new OllamaAssistantLlmClient(http, _envModule.AssistantLlmModel);
                });
            }
            services.AddScoped<IAssistantService>(sp =>
                new AssistantService(sp.GetService<IAssistantLlmClient>()));

            // === Telemetry: OpenTelemetry OTLP ===
            // Base traces + metrics (HttpClient + .NET runtime + BuildingOS.Pipeline meter).
            // OTEL_TRACES_SAMPLER_ARG controls the trace sampling ratio (0.0–1.0, default 1.0).
            var sampleRatio = double.TryParse(
                Configuration["OTEL_TRACES_SAMPLER_ARG"],
                NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ? r : 1.0;
            services.AddOtlpTelemetry(_envModule.OtlpServiceName, _envModule.OtlpEndpoint, sampleRatio);
            // Add ASP.NET Core server instrumentation (http_server_* request duration /
            // active requests) on top — kept here so Shared stays free of an AspNetCore
            // framework dependency. AddOpenTelemetry() is idempotent and returns the same builder.
            // Health-check paths are filtered from traces to avoid sampling noise.
            if (!string.IsNullOrEmpty(_envModule.OtlpEndpoint))
            {
                services.AddOpenTelemetry()
                    .WithTracing(builder => builder.AddAspNetCoreInstrumentation(opts =>
                        opts.Filter = ctx => !ctx.Request.Path.StartsWithSegments(HealthPath)))
                    .WithMetrics(builder => builder.AddAspNetCoreInstrumentation());
            }

            // === Logger middleware ===
            services.AddSingleton<LoggerMiddlewareOption>(provider => new LoggerMiddlewareOption
            {
                FilteringPaths = new[]
                {
                    HealthPath,
                    "/metrics",
                    "/favicon.ico",
                    "/swagger",
                    "/redoc"
                },
                LogRequestBody = _envModule.AspNetCoreEnvironment == "Development",
                LogResponseBody = false,
                LogHeaders = false,
                DeserializeRequest = requestBody =>
                {
                    if (string.IsNullOrWhiteSpace(requestBody)) return null!;
                    try
                    {
                        return JsonSerializer.Deserialize<object>(requestBody,
                            JsonSerializerHelper.JsonSerializerOptions) ?? "{}";
                    }
                    catch
                    {
                        return requestBody.Length > 1000 ? requestBody[..1000] + "..." : requestBody;
                    }
                },
                DeserializeResponse = responseBody =>
                {
                    if (string.IsNullOrWhiteSpace(responseBody)) return null!;
                    try
                    {
                        return JsonSerializer.Deserialize<object>(responseBody,
                            JsonSerializerHelper.JsonSerializerOptions) ?? "{}";
                    }
                    catch
                    {
                        return responseBody.Length > 500 ? responseBody[..500] + "..." : responseBody;
                    }
                }
            });

            // === NATS (control command / result bus) ===
            var nats = new NATS.Client.Core.NatsConnection(
                new NATS.Client.Core.NatsOpts { Url = _envModule.NatsUrl });
            services.AddSingleton<NATS.Client.Core.INatsConnection>(nats);
            services.AddSingleton<Services.IControlResultBus, Services.NatsControlResultBus>();
            services.AddSingleton<BuildingOS.Shared.Infrastructure.PointControl.IPointControlCommandPublisher,
                Services.NatsPointControlCommandPublisher>();

            // === Control egress routing: gatewayId → GatewayConnection (binding + settings) ===
            // Dedicated config (plan §1 案A, #154 Phase 2). Default "hono" preserves the previous
            // behaviour for unmapped gateways; per-gateway binding overrides live under
            // GatewayConnectionTypes:Map and per-gateway settings under Gateways:{id}:Settings.
            // The ApiServer only reads the binding (to pick the ControlType); the worker uses the
            // settings. Both build the same registry via the shared factory.
            services.AddSingleton<IGatewayConnectionRegistry>(
                GatewayConnectionRegistryFactory.Create(Configuration));
            services.AddSingleton<IControlTypeResolver, ControlTypeResolver>();

            // Point-list resync push (gateway admin #323): publishes building-os.pointlist.updated.gw.{id}.
            services.AddSingleton<IPointListUpdatePublisher, NatsPointListUpdatePublisher>();

            services
                .AddMemoryCache()
                .AddSingleton(_envModule)
                .AddCorsForAll(Configuration)
                .AddAuth()
                .AddControllers();

            // === Auth ===
            if (!_envModule.DisableAuth)
            {
                services.AddOidcAuthentication(_envModule);
            }
            else
            {
                services.AddAuthentication("Test")
                    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions,
                        TestAuthenticationHandler>(
                        "Test", options => { });
                services.AddAuthorization(options =>
                {
                    options.DefaultPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder("Test")
                        .RequireAssertion(_ => true)
                        .Build();
                });
            }

            // === gRPC (device control streaming) ===
            services.AddGrpc();
            services.AddEndpointsApiExplorer();
            services.AddSwagger();
        }

        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            // EF Core migrations require session-scoped advisory locks.
            // Use POSTGRES_MIGRATION_CONNECTION_STRING (session pool or direct) instead of
            // the transaction-pool connection used for regular app traffic.
            using (var scope = app.ApplicationServices.CreateScope())
            {
                var dbContext = scope.ServiceProvider.GetService<RelationalDbContext>();
                if (dbContext is not null
                    && !string.IsNullOrEmpty(_envModule.PostgresMigrationConnectionString)
                    && _envModule.PostgresMigrationConnectionString != _envModule.PostgresConnectionString)
                {
                    dbContext.Database.SetConnectionString(_envModule.PostgresMigrationConnectionString);
                }
                dbContext?.Database.Migrate();
            }

            app.UseRouting();
            app.UseCors(IServiceCollectionExtension.MyAllowSpecificOrigins);
            app.UseGrpcWeb();
            app.UseMiddleware<LoggerMiddleware>();
            app.UseMiddleware<BasicAuthenticationMiddleware>();
            app.UseAuthentication();
            app.UseMiddleware<AuthorizationContextMiddleware>();
            app.UseAuthorization();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapGrpcService<Services.GreeterService>()
                    .EnableGrpcWeb()
                    .RequireCors(IServiceCollectionExtension.MyAllowSpecificOrigins);
                endpoints.MapGrpcService<Services.PointControlGrpcService>()
                    .EnableGrpcWeb()
                    .RequireCors(IServiceCollectionExtension.MyAllowSpecificOrigins);

                endpoints.MapControllers();

                endpoints.MapGet(HealthPath, async context =>
                {
                    // Environment name is deliberately not exposed here: /health is
                    // anonymous and leaking Production/Staging is a minor information
                    // disclosure (#18 Phase 1).
                    var healthData = new
                    {
                        Status = "Healthy",
                        Timestamp = DateTime.UtcNow,
                        Version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0"
                    };
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(
                        JsonSerializer.Serialize(healthData, JsonSerializerHelper.JsonSerializerOptions),
                        context.RequestAborted);
                });
            });

            app.UseSwagger();
            app.UseSwaggerUI(options =>
            {
                options.SwaggerEndpoint("/swagger/building-os/swagger.json", "building-os");
            });
            app.UseReDoc(x =>
            {
                x.SpecUrl = "/swagger/building-os/swagger.json";
                x.DocumentTitle = "Building OS API";
                x.RoutePrefix = "api-docs";
            });
        }
    }
}
