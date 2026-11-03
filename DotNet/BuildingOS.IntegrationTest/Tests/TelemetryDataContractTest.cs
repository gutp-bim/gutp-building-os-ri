using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using BuildingOS.ConnectorWorker.Connectors;
using BuildingOS.IntegrationTest.Common.Fixtures;
using BuildingOS.Shared;
using BuildingOS.Shared.Domain.Authorization;
using BuildingOS.Shared.Infrastructure;
using BuildingOS.Shared.Infrastructure.BlobStorage;
using BuildingOS.Shared.Infrastructure.Oss;
using BuildingOS.Shared.Infrastructure.Telemetry;
using BuildingOS.Shared.Infrastructure.Telemetry.ParquetLake;
using BuildingOs.ApiServer.Controllers;
using BuildingOs.ApiServer.Middlewares;
using BuildingOs.ApiServer.Telemetry;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using Moq;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using Xunit;

namespace BuildingOS.IntegrationTest.Tests;

// Dedicated, disposable NATS. Never connects to the OSS stack or a configured external broker.
public class TelemetryDataContractTest(NatsFixture fixture) : IClassFixture<NatsFixture>
{
    private const string Subject = "building-os.validated.telemetry";
    private const string Building = "bldg-boptest";
    private static readonly JsonSerializerOptions WireJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    [Theory]
    [InlineData(35)]
    [InlineData(500)]
    public async Task ClockData_SurvivesRealKvAndAuthorizedHttpLatestAndRawHistory(int pointCount)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = timeout.Token;
        var (connection, js) = await fixture.CreateJetStreamAsync();
        await using var connectionLease = connection;
        var hot = new NatsKvLatestStore(js, NullLogger<NatsKvLatestStore>.Instance);
        var bus = new NatsIngressTelemetryBus(js, hot, NullLogger<NatsIngressTelemetryBus>.Instance);
        var now = DateTime.UtcNow;
        var message = CreateClockMessage(pointCount, now);
        await bus.PublishAsync(Subject, message, ct);

        // Read the bytes persisted by the actual bus, then use the real Parquet serializer/reader.
        var consumer = await js.CreateOrUpdateConsumerAsync("BUILDING_OS_VALIDATED", new ConsumerConfig($"data-contract-{pointCount}")
        {
            FilterSubject = Subject, DeliverPolicy = ConsumerConfigDeliverPolicy.Last,
            AckPolicy = ConsumerConfigAckPolicy.None,
        }, ct);
        string? persisted = null;
        await foreach (var item in consumer.FetchAsync<byte[]>(opts: new NatsJSFetchOpts { MaxMsgs = 1 }, cancellationToken: ct))
            persisted = Encoding.UTF8.GetString(item.Data!);
        Assert.Equal(message, persisted);
        var rows = ValidTelemetryEnvelope.Parse(persisted!);
        Assert.Equal(pointCount, rows.Count);
        await VerifyApiAsync(hot, rows, message, now, actualKv: true, $"sizes-real-kv-{pointCount}.json", ct);
    }

    internal static async Task VerifyApiAsync(IHotTelemetryStore hot, IReadOnlyList<ValidTelemetryData> rows,
        string message, DateTime now, bool actualKv, string artifactName, CancellationToken ct)
    {
        var pointCount = rows.Count;
        var pointIds = rows.Select(r => r.PointId!).ToArray();
        using var blob = new LocalBlobStorage();
        await using (var stream = new MemoryStream())
        {
            await ParquetTelemetrySerializer.WriteAsync(rows, stream, ct);
            stream.Position = 0;
            await blob.PutAsync("cold", LakePartitionKey.For(Building, now, 1, 1), stream, cancellationToken: ct);
        }
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var lake = new ParquetLakeTelemetryStore(blob, cache, new(), NullLogger<ParquetLakeTelemetryStore>.Instance);
        var router = new OssTelemetryQueryRouter(NullLogger<OssTelemetryQueryRouter>.Instance, cache, hot, lake, lake);
        var key = new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(64));
        await using var app = CreateApi(router, pointIds, key);
        await app.StartAsync(ct);
        using var client = new HttpClient { BaseAddress = new Uri(Assert.Single(app.Urls)) };
        var query = $"/api/v1/telemetries/query?pointId={pointIds[0]}&latest=true";
        using (var anonymous = await client.GetAsync(query, ct)) Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "invalid");
        using (var invalid = await client.GetAsync(query, ct)) Assert.Equal(HttpStatusCode.Unauthorized, invalid.StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", IssueJwt(key, false));
        using (var denied = await client.GetAsync(query, ct)) Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", IssueJwt(key, true));
        var latestReadings = new List<JsonElement>();
        var maxKvBytes = 0;
        foreach (var row in rows)
        {
            var latest = await ReadOneAsync(client, $"/api/v1/telemetries/query?pointId={row.PointId}&latest=true", ct);
            // 35 synthetic points get independent HTTP raw queries too; 500 is a latest fan-out size probe.
            if (pointCount == 35)
            {
                var start = Uri.EscapeDataString(now.AddMinutes(-1).ToString("O"));
                var end = Uri.EscapeDataString(now.AddMinutes(1).ToString("O"));
                var raw = await ReadOneAsync(client, $"/api/v1/telemetries/query?pointId={row.PointId}&start={start}&end={end}", ct);
                Assert.Equal(raw.GetProperty("data").GetString(), latest.GetProperty("data").GetString());
                Assert.Equal(raw.GetProperty("value").GetDouble(), latest.GetProperty("value").GetDouble());
                Assert.Equal(DateTimeOffset.Parse(raw.GetProperty("datetime").GetString()!),
                    DateTimeOffset.Parse(latest.GetProperty("datetime").GetString()!));
            }
            Assert.Equal(row.Data, latest.GetProperty("data").GetString());
            Assert.Equal(21.5, latest.GetProperty("value").GetDouble());
            Assert.Equal(new DateTimeOffset(now), DateTimeOffset.Parse(latest.GetProperty("datetime").GetString()!));
            latestReadings.Add(latest);
            maxKvBytes = Math.Max(maxKvBytes, JsonSerializer.SerializeToUtf8Bytes((await hot.GetAsync(row.PointId!, ct))!).Length);
        }
        // Raw batch and batch-latest both carry the stored Data as a JSON string.
        using var batch = await client.PostAsJsonAsync("/api/v1/telemetries/query/batch",
            new { pointIds, start = now.AddMinutes(-1), end = now.AddMinutes(1) }, ct);
        batch.EnsureSuccessStatusCode();
        var batchBytes = await batch.Content.ReadAsByteArrayAsync(ct);
        using var batchDocument = JsonDocument.Parse(batchBytes);
        Assert.Equal(pointCount, batchDocument.RootElement.GetArrayLength());
        foreach (var series in batchDocument.RootElement.EnumerateArray())
            Assert.NotNull(Assert.Single(series.GetProperty("readings").EnumerateArray()).GetProperty("data").GetString());
        using var compact = await client.PostAsJsonAsync("/api/v1/telemetries/query/batch-latest", new { pointIds }, ct);
        compact.EnsureSuccessStatusCode();
        var compactBytes = await compact.Content.ReadAsByteArrayAsync(ct);
        using var compactDocument = JsonDocument.Parse(compactBytes);
        Assert.Equal(pointCount, compactDocument.RootElement.GetArrayLength());
        foreach (var sample in compactDocument.RootElement.EnumerateArray())
        {
            var expected = Assert.Single(rows, row => row.PointId == sample.GetProperty("pointId").GetString());
            Assert.Equal(expected.Data, sample.GetProperty("data").GetString());
        }
        var measurements = new
        {
            pointCount, dataBytes = Encoding.UTF8.GetByteCount(rows[0].Data!),
            maxSerializedHotRowBytes = maxKvBytes, envelopeBytes = Encoding.UTF8.GetByteCount(message),
            authenticatedRawBatchResponseBytes = batchBytes.Length,
            authenticatedCompactLatestResponseBytes = compactBytes.Length,
            authenticatedIndividualLatestResponseBytesSum = latestReadings.Sum(r => Encoding.UTF8.GetByteCount(r.GetRawText()) + 2),
            actualKv, actualHttp = true, jwtSignatureValidated = true,
            rawHistoryHttpPoints = pointCount == 35 ? 35 : 0,
            parquetStorage = "temporary local files", metadataAndHierarchy = "fixtures",
        };
        var artifactDirectory = Path.Combine(AppContext.BaseDirectory, "TelemetryContractArtifacts");
        Directory.CreateDirectory(artifactDirectory);
        await File.WriteAllTextAsync(Path.Combine(artifactDirectory, artifactName),
            JsonSerializer.Serialize(measurements, new JsonSerializerOptions { WriteIndented = true }), ct);
        await app.StopAsync(ct);
    }

    [Fact]
    public async Task RaisedLimits_CanFitValidatedPublishButExceedKvPayload_LeavingPreviousLatest()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;
        var (connection, js) = await fixture.CreateJetStreamAsync();
        await using var connectionLease = connection;
        var hot = new NatsKvLatestStore(js, NullLogger<NatsKvLatestStore>.Instance);
        const string point = "size-escape-probe";
        await hot.PutAsync(point, new ValidTelemetryData { PointId = point, Value = 1 }, ct);
        var message = CreateEscapedMessage();
        Assert.True(Encoding.UTF8.GetByteCount(message) < 1_048_576);
        var projected = Assert.Single(ValidTelemetryEnvelope.Parse(message));
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(projected).Length > 1_048_576);
        var logger = new WarningLogger<NatsIngressTelemetryBus>();
        var bus = new NatsIngressTelemetryBus(js, hot, logger, new TelemetrySizeLimits(600_000, 700_000));
        // The existing contract accepts persisted telemetry even if the best-effort Hot update fails.
        await bus.PublishAsync(Subject, message, ct);
        Assert.Single(logger.Warnings);
        Assert.Equal(1, (await hot.GetAsync(point, ct))!.Value);
        Assert.Equal(2, projected.Value);
    }

    internal static string CreateEscapedMessage() => Envelope(new[]
    {
        new { point_id = "size-escape-probe", value = 2, data = new { note = new string('"', 180_000) } },
    });

    internal static string CreateClockMessage(int pointCount, DateTime now) => Envelope(
        Enumerable.Range(2, pointCount).Select(i => new
        {
            id = $"observation-BPT-{i:D4}", point_id = $"BPT-{i:D4}", building = Building,
            device_id = "DEV-FCU-1", name = "temperature", value = 21.5, datetime = now.ToString("O"), data = ClockData(),
        }));

    private static object ClockData() => new
    {
        gatewayId = "GW-BOPTEST-001", sim_clock_id = "urn:boptest:clock:bestest-air",
        sim_config_revision = "fixture-revision", sim_time_s = "3600.0",
        sim_time_quality = "stable_unfenced", sim_run_status = "absent",
    };

    private static string Envelope<T>(IEnumerable<T> rows) => JsonSerializer.Serialize(new { telemetries = rows }, WireJson);

    private static WebApplication CreateApi(ITelemetryQueryRouter router, string[] pointIds, SecurityKey key)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddControllers().AddApplicationPart(typeof(TelemetryController).Assembly);
        builder.Services.AddMemoryCache();
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
        {
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true, ValidIssuer = "https://issuer.example.com",
                ValidateAudience = true, ValidAudience = "ri-contract-test",
                ValidateLifetime = true, ValidateIssuerSigningKey = true, IssuerSigningKey = key,
                ClockSkew = TimeSpan.Zero,
            };
        });
        builder.Services.AddAuthorization();
        var twin = new Mock<IDigitalTwinDatabase>();
        twin.Setup(t => t.GetPoint(It.IsAny<string>())).ReturnsAsync((string id) =>
            pointIds.Contains(id) ? new Point { Id = id, DtId = "urn:point:" + id, Name = id } : null);
        builder.Services.AddSingleton(twin.Object);
        builder.Services.AddSingleton(Mock.Of<ITelemetryDatabase>());
        builder.Services.AddSingleton(router);
        var hierarchy = new Mock<IResourceHierarchyResolver>();
        hierarchy.Setup(h => h.GetAncestorsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { ("building", Building) });
        var groups = new Mock<IGroupMembershipResolver>();
        groups.Setup(g => g.GetGroupsContainingResourceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());
        builder.Services.AddSingleton(hierarchy.Object);
        builder.Services.AddSingleton(groups.Object);
        builder.Services.AddSingleton<BuildingOS.Shared.Domain.Authorization.IAuthorizationService, DefaultAuthorizationService>();
        var app = builder.Build();
        app.UseAuthentication();
        app.UseMiddleware<AuthorizationContextMiddleware>();
        app.UseAuthorization();
        app.MapControllers();
        return app;
    }

    private static string IssueJwt(SecurityKey key, bool canRead)
    {
        var claims = new List<Claim> { new("sub", canRead ? "reader" : "denied"), new("building_os_role", "operator") };
        if (canRead) claims.Add(new Claim("permissions", PermissionHelper.BuildPermissionString("building", Building, "read")));
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            issuer: "https://issuer.example.com", audience: "ri-contract-test", claims: claims,
            notBefore: DateTime.UtcNow.AddMinutes(-1), expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256)));
    }

    private static async Task<JsonElement> ReadOneAsync(HttpClient client, string path, CancellationToken ct)
    {
        using var response = await client.GetAsync(path, ct);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct));
        return Assert.Single(document.RootElement.EnumerateArray()).Clone();
    }

    private sealed class WarningLogger<T> : ILogger<T>
    {
        public List<string> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (level == LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }

    private sealed class LocalBlobStorage : IBlobStorage, IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "ri607-" + Guid.NewGuid().ToString("N"));
        public async Task PutAsync(string container, string key, Stream content, string contentType = "application/octet-stream", CancellationToken cancellationToken = default)
        {
            var path = Path.Combine(root, container, key);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var file = File.Create(path);
            await content.CopyToAsync(file, cancellationToken);
        }
        public Task<Stream?> GetAsync(string container, string key, CancellationToken cancellationToken = default)
        {
            var path = Path.Combine(root, container, key);
            return Task.FromResult<Stream?>(File.Exists(path) ? File.OpenRead(path) : null);
        }
        public Task<IReadOnlyList<string>> ListAsync(string container, string prefix = "", CancellationToken cancellationToken = default)
        {
            var directory = Path.Combine(root, container);
            return Task.FromResult<IReadOnlyList<string>>(Directory.Exists(directory)
                ? Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(directory, p))
                    .Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToArray() : []);
        }
        public Task<bool> ExistsAsync(string container, string key, CancellationToken cancellationToken = default)
            => Task.FromResult(File.Exists(Path.Combine(root, container, key)));
        public Task DeleteAsync(string container, string key, CancellationToken cancellationToken = default)
        {
            File.Delete(Path.Combine(root, container, key));
            return Task.CompletedTask;
        }
        public void Dispose()
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}

// Runs even when a Docker registry outage blocks the real-KV fixture. This is explicitly a component
// test: real Hot projection, HTTP/JWT, Parquet serializer/reader, with an in-memory Hot adapter.
public class TelemetryDataHttpContractTest
{
    [Theory]
    [InlineData(35)]
    [InlineData(500)]
    public async Task ClockData_SurvivesHotProjectionAndAuthorizedHttp_WithMemoryAdapter(int pointCount)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var now = DateTime.UtcNow;
        var message = TelemetryDataContractTest.CreateClockMessage(pointCount, now);
        var hot = new MemoryHotStore();
        await ValidatedTelemetryHotStore.WriteAsync(hot, message, "gateway-grpc", NullLogger.Instance, timeout.Token);
        await TelemetryDataContractTest.VerifyApiAsync(hot, ValidTelemetryEnvelope.Parse(message), message, now,
            actualKv: false, $"sizes-component-{pointCount}.json", timeout.Token);
    }

    [Fact]
    public void RaisedLimits_ValidateWireSizeButDoNotBoundKvEscaping()
    {
        var message = TelemetryDataContractTest.CreateEscapedMessage();
        new TelemetrySizeLimits(600_000, 700_000).Validate(message);
        Assert.True(Encoding.UTF8.GetByteCount(message) < 1_048_576);
        var row = Assert.Single(ValidTelemetryEnvelope.Parse(message));
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(row).Length > 1_048_576);
    }

    private sealed class MemoryHotStore : IHotTelemetryStore
    {
        private readonly Dictionary<string, ValidTelemetryData> rows = new();
        public Task PutAsync(string pointId, ValidTelemetryData data, CancellationToken cancellationToken = default)
        {
            rows[pointId] = data;
            return Task.CompletedTask;
        }
        public Task<ValidTelemetryData?> GetAsync(string pointId, CancellationToken cancellationToken = default)
            => Task.FromResult(rows.GetValueOrDefault(pointId));
    }
}
