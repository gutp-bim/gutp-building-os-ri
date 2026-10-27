using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using BuildingOS.IntegrationTest.Common.Fixtures;
using BuildingOS.Shared;
using BuildingOS.Shared.Domain;
using BuildingOS.Shared.Domain.Authorization;
using BuildingOS.Shared.Domain.PointControl;
using BuildingOS.Shared.Infrastructure;
using BuildingOS.Shared.Infrastructure.ControlRouting;
using BuildingOS.Shared.Infrastructure.OxiGraph;
using BuildingOS.Shared.Infrastructure.PointControl;
using BuildingOS.Shared.Infrastructure.PointControlRepository;
using BuildingOs.ApiServer.Authorization;
using BuildingOs.ApiServer.Controllers;
using BuildingOs.ApiServer.Middlewares;
using BuildingOs.ApiServer.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Moq;
using Xunit;

namespace BuildingOS.IntegrationTest.Tests;

public class NativeBoundsV1ContractTest(OxiGraphFixture store) : IClassFixture<OxiGraphFixture>, IAsyncLifetime
{
    private const string Building = "https://example.com/native-bounds/building";
    private const string Prefix = "@prefix ex: <https://example.com/native-bounds/> . @prefix sbco: <https://www.sbco.or.jp/ont/> . ";
    public Task InitializeAsync() => store.ClearAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData("278.15", "308.15")]
    [InlineData(null, "1.5")]
    [InlineData("2147483648", null)]
    [InlineData("-2147483649", "1")]
    [InlineData("NaN", "Infinity")]
    [InlineData("1.00000000000000000000000001", "2")]
    [InlineData("1e-1000", "2")]
    [InlineData("", "1")]
    [InlineData("not-a-number", "1")]
    public async Task UnrepresentableBounds_ReturnConflict_FromAllV1PointReads(string? min, string? max)
    {
        await SeedAsync(min, max);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var db = new OxiGraphDigitalTwinDatabase(store.Client, cache);
        var key = new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32));
        await using var app = CreateApi(db, key);
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", IssueJwt(key, "admin"));

        foreach (var path in ReadPaths())
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("native_bounds_not_representable", body.RootElement.GetProperty("error").GetString());
            Assert.False(body.RootElement.TryGetProperty("point", out _));
        }
        // RDF remains accepted and the mapper stays usable by non-v1 consumers such as control.
        Assert.NotNull(await db.GetPoint("P1"));
    }

    [Theory]
    [InlineData("1.0", "2e1", 1, 20)]
    [InlineData("-2147483648", "2147483647", int.MinValue, int.MaxValue)]
    [InlineData("0", "1", 0, 1)]
    [InlineData("-0", "1.5e1", 0, 15)]
    public async Task ExactlyIntegralNumericLiterals_ReturnIntegersWithoutRounding(string min, string max, int expectedMin, int expectedMax)
    {
        await SeedAsync(min, max);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var db = new OxiGraphDigitalTwinDatabase(store.Client, cache);
        var key = new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32));
        await using var app = CreateApi(db, key);
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", IssueJwt(key, "admin"));
        using var response = await client.GetAsync("/api/v1/point-details/P1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var point = body.RootElement.GetProperty("point");
        Assert.Equal(expectedMin, point.GetProperty("minPresValue").GetInt32());
        Assert.Equal(expectedMax, point.GetProperty("maxPresValue").GetInt32());
        Assert.False(point.TryGetProperty("hasUnrepresentableNativeBounds", out _));
    }

    private static string[] ReadPaths() => ["/api/v1/points", "/api/v1/points/P1",
        "/api/v1/point-details/P1", "/api/v1/point-details?buildingDtId=" + Uri.EscapeDataString(Building)];

    [Fact]
    public async Task MissingBounds_ReturnNull_AndAuthorizationPrecedesRepresentationErrors()
    {
        await SeedAsync(null, null);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var db = new OxiGraphDigitalTwinDatabase(store.Client, cache);
        var key = new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32));
        await using var app = CreateApi(db, key);
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", IssueJwt(key, "admin"));
        foreach (var path in ReadPaths())
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var item = body.RootElement.ValueKind == JsonValueKind.Array ? Assert.Single(body.RootElement.EnumerateArray()) : body.RootElement;
            var point = item.TryGetProperty("point", out var nested) ? nested : item;
            Assert.Equal(JsonValueKind.Null, point.GetProperty("minPresValue").ValueKind);
            Assert.Equal(JsonValueKind.Null, point.GetProperty("maxPresValue").ValueKind);
        }
        await store.ClearAsync();
        await SeedAsync("278.15", "308.15");
        client.DefaultRequestHeaders.Authorization = null;
        foreach (var path in ReadPaths())
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", IssueJwt(key, "operator"));
        foreach (var path in ReadPaths())
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.DoesNotContain("native_bounds_not_representable", await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task BuilderRdf_RemainsImportable_AndRejectsOnlyUnrepresentableV1Reads()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "BuilderClockFixtures");
        await new OxiGraphIngestMaterializer(store.Client).MaterializeAsync(File.ReadAllText(Path.Combine(directory, "model.ttl")));
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var db = new OxiGraphDigitalTwinDatabase(store.Client, cache);
        var key = new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32));
        await using var app = CreateApi(db, key);
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", IssueJwt(key, "admin"));
        using var csv = new Microsoft.VisualBasic.FileIO.TextFieldParser(Path.Combine(directory, "pointlist-v2.csv"));
        csv.SetDelimiters(",");
        var header = csv.ReadFields()!;
        var ids = new[] { "BPT-0033", "BPT-0034", "BPT-0035", "BPT-0036" };
        var seen = new HashSet<string>();
        while (!csv.EndOfData)
        {
            var row = csv.ReadFields()!;
            var id = row[Array.IndexOf(header, "point_id")];
            if (!ids.Contains(id)) continue;
            seen.Add(id);
            var point = await db.GetPoint(id);
            Assert.NotNull(point);
            using var response = await client.GetAsync("/api/v1/point-details/" + id);
            Assert.Equal(id == "BPT-0035" ? HttpStatusCode.OK : HttpStatusCode.Conflict, response.StatusCode);
            if (id == "BPT-0035")
            {
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.Equal(int.Parse(row[Array.IndexOf(header, "min_pres_value")]), body.RootElement.GetProperty("point").GetProperty("minPresValue").GetInt32());
                Assert.Equal(int.Parse(row[Array.IndexOf(header, "max_pres_value")]), body.RootElement.GetProperty("point").GetProperty("maxPresValue").GetInt32());
            }
        }
        Assert.Equal(ids.OrderBy(id => id), seen.OrderBy(id => id));
        Assert.Equal(35, (await db.ListPoints(null)).Length);
        using var batch = await client.GetAsync("/api/v1/point-details?buildingDtId=" + Uri.EscapeDataString("https://www.sbco.or.jp/ont/resource/bldg-boptest"));
        Assert.Equal(HttpStatusCode.Conflict, batch.StatusCode);
    }

    [Fact]
    public async Task ControlStillUsesControlSchema_WhenNativeBoundsCannotBeReadByV1()
    {
        await SeedAsync("278.15", "308.15");
        await store.Client.ImportTurtleAsync(Prefix + """@prefix bos: <http://buildingos.gutp.jp/ontology#> . ex:point bos:dataType "number" ; bos:minValue 0 ; bos:maxValue 100 .""");
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var db = new OxiGraphDigitalTwinDatabase(store.Client, cache);
        var key = new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32));
        await using var app = CreateApi(db, key);
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", IssueJwt(key, "admin"));
        using var accepted = await client.PostAsJsonAsync("/api/v1/points/P1/control", new { value = 21.5 });
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        using var rejected = await client.PostAsJsonAsync("/api/v1/points/P1/control", new { value = 150 });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
    }

    private Task SeedAsync(string? min, string? max) => store.Client.ImportTurtleAsync(Prefix + $$"""
        ex:building a sbco:Building ; sbco:id "B1" ; sbco:name "Building" ; sbco:hasPart ex:floor .
        ex:floor a sbco:Level ; sbco:id "F1" ; sbco:name "Floor" .
        ex:device a sbco:EquipmentExt ; sbco:id "D1" ; sbco:name "Device" ; sbco:locatedIn ex:floor ; sbco:hasPoint ex:point .
        ex:point a sbco:PointExt ; sbco:id "P1" ; sbco:name "Point" {{(min is null ? "" : "; sbco:minPresValue \"" + min + "\"")}} {{(max is null ? "" : "; sbco:maxPresValue \"" + max + "\"")}} .
        """);

    private WebApplication CreateApi(IDigitalTwinDatabase db, SecurityKey key)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddControllers().AddApplicationPart(typeof(PointDetailController).Assembly);
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
        {
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true, ValidIssuer = "https://issuer.example.com",
                ValidateAudience = true, ValidAudience = "native-bounds-test",
                ValidateLifetime = true, ValidateIssuerSigningKey = true, IssuerSigningKey = key,
                ClockSkew = TimeSpan.Zero,
            };
        });
        builder.Services.AddAuthorization();
        builder.Services.AddMemoryCache();
        builder.Services.AddSingleton(db);
        builder.Services.AddSingleton(store.Client);
        builder.Services.AddSingleton(Mock.Of<IAuthorizationService>());
        builder.Services.AddScoped<IAuthorizedTwinView, AuthorizedTwinView>();
        builder.Services.AddSingleton<IControlSchemaResolver, BuildingOS.Shared.Infrastructure.Oss.OssControlSchemaResolver>();
        // Test dispatch only: no real gateway or device is contacted.
        var dispatch = new Mock<IControlTypeResolver>();
        dispatch.Setup(r => r.Resolve(It.IsAny<Point>(), It.IsAny<Device?>(), It.IsAny<double>()))
            .Returns(new ControlDispatch("test", "{}", "GW-EXAMPLE"));
        builder.Services.AddSingleton(dispatch.Object);
        builder.Services.AddSingleton(Mock.Of<IControlResultBus>());
        builder.Services.AddSingleton(Mock.Of<IPointControlCommandPublisher>());
        builder.Services.AddSingleton(Mock.Of<IPointControlRepository>());
        builder.Services.AddSingleton(Mock.Of<IControlAuditWriter>());
        builder.Services.AddSingleton(new ControlSafetyOptions(ControlSchemaFailurePolicy.Allow));
        var app = builder.Build();
        app.UseAuthentication();
        app.UseMiddleware<AuthorizationContextMiddleware>();
        app.UseAuthorization();
        app.MapControllers();
        return app;
    }

    private static string IssueJwt(SecurityKey key, string role) => new JwtSecurityTokenHandler().WriteToken(
        new JwtSecurityToken(issuer: "https://issuer.example.com", audience: "native-bounds-test",
            claims: new[] { new Claim("sub", "test-user"), new Claim("building_os_role", role) },
            notBefore: DateTime.UtcNow.AddMinutes(-1), expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256)));
}
