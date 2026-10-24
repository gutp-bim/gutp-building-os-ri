using System.Text.Json;
using BuildingOs.ApiServer.GatewayProvisioning;
using BuildingOS.IntegrationTest.Common;
using BuildingOS.IntegrationTest.Common.Fixtures;
using BuildingOS.Shared.Domain.GatewayProvisioning;
using BuildingOS.Shared.Domain.TwinAdmin;
using BuildingOS.Shared.Infrastructure.OxiGraph;
using BuildingOS.Shared.Infrastructure;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace BuildingOS.IntegrationTest.Tests;

public sealed class ClockProvisioningTest(OxiGraphFixture store)
    : IntegrationTestBase, IClassFixture<OxiGraphFixture>, IAsyncLifetime
{
    private const string Prefix = "@prefix ex: <https://example.com/ri-606/> . @prefix bos: <http://buildingos.gutp.jp/ontology#> . @prefix sbco: <https://www.sbco.or.jp/ont/> . ";
    private static string Fixture => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ClockFixtures/model.ttl"));
    private OxiGraphIngestMaterializer Materializer => new(store.Client);
    private OxiGraphGatewayProvisioningSource Source => new(store.Client);
    public Task InitializeAsync() => store.ClearAsync();
    public Task DisposeAsync() => Task.CompletedTask;
    private async Task<GatewayConfigurationResponse> ReadAsync(string gatewayId = "GW-EXAMPLE") =>
        GatewayConfigurationResponse.From(gatewayId, await Source.ReadAsync(gatewayId, default));

    [Fact]
    public async Task FixtureImportsIdempotently_AndMatchesProvisioningContract()
    {
        await Materializer.MaterializeAsync(Fixture);
        var first = await ReadAsync();
        Assert.Equal(35, first.Points.Length);
        Assert.Equal(31, first.Points.Count(p => p.Writable == false));
        Assert.Equal(4, first.Points.Count(p => p.Writable == true));
        Assert.Single(first.ClockSources);
        Assert.Equal(2, first.ClockBindings.Length);
        Assert.DoesNotContain(first.Points, p => p.PointId == "BPT-0001");
        using var expected = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ClockFixtures/provisioning.json")));
        Assert.Equal(expected.RootElement.GetProperty("revision").GetString(), first.Revision);
        await Materializer.MaterializeAsync(Fixture);
        Assert.Equal(first.Revision, (await ReadAsync()).Revision);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var database = new OxiGraphDigitalTwinDatabase(store.Client, cache);
        var legacyPoints = await database.ListGatewayPointList("GW-EXAMPLE");
        Assert.Equal(35, (await database.ListPoints(null)).Length);
        Assert.Null(await database.GetPoint(first.ClockSources[0].ClockId));
        var search = await database.SearchResources(null, "point", null, [], 100, 0);
        Assert.Equal(35, search.Length);
        Assert.DoesNotContain(search, p => p.DtId == first.ClockSources[0].ClockId);
        Assert.Equal(35, legacyPoints.Length);
        Assert.Equal(PointListEtag.Compute(legacyPoints), PointListEtag.Compute((await Source.ReadAsync("GW-EXAMPLE", default)).Points));
        var other = await ReadAsync("GW-OTHER");
        Assert.Empty(other.Points);
        Assert.Empty(other.ClockSources);
        Assert.Empty(other.ClockBindings);
        Assert.Empty((await ReadAsync("GW-OTHER\" } UNION { ?s ?p ?o } #")).Points);
        var leftovers = await store.Client.QueryAsync("SELECT DISTINCT ?g WHERE { GRAPH ?g { ?s ?p ?o } FILTER (?g != <urn:bos:twin-source>) }");
        Assert.Empty(leftovers);
    }

    [Theory]
    [InlineData("ex:equipment-a bos:usesClockSource ex:missing .", "multiple_or_invalid_clocks")]
    [InlineData("ex:equipment-a bos:usesClockSource \"https://example.com/ri-606/clock/shared-simulation\" .", "multiple_or_invalid_clocks")]
    [InlineData("ex:BPT-0002 sbco:id \"BPT-0003\" .", "invalid_point_id")]
    [InlineData("ex:BPT-0002 sbco:gatewayId \"GW-OTHER\" .", "gateway_mismatch")]
    [InlineData("ex:clock-native bos:deviceInstance -1 .", "invalid_deviceInstance")]
    [InlineData("ex:clock-native bos:deviceInstance \"1234\" .", "invalid_deviceInstance")]
    [InlineData("ex:clock-native bos:property \"other\" .", "invalid_native_source")]
    [InlineData("ex:equipment-b sbco:hasPoint ex:BPT-0002 .", "multiple_equipment")]
    [InlineData("ex:unknown bos:usesClockSource <https://example.com/ri-606/clock/shared-simulation> .", "invalid_equipment")]
    [InlineData("ex:BPT-0002 bos:usesClockSource <https://example.com/ri-606/clock/shared-simulation> .", "invalid_equipment")]
    [InlineData("<https://example.com/ri-606/clock/shared-simulation> bos:runId \"run\" .", "unexpected_static_property")]
    [InlineData("<https://example.com/ri-606/clock/shared-simulation> a sbco:PointExt .", "clock_is_point")]
    public async Task InvalidAppend_IsReportedByPreview_AndCannotMutateTwin(string addition, string reason)
    {
        await Materializer.MaterializeAsync(Fixture);
        var before = await ReadAsync();
        var service = new OxiGraphTwinAdminService(store.Client, Materializer);
        var preview = await service.PreviewImportAsync(Prefix + addition, TwinImportMode.Append);
        Assert.True(preview.ClockIssueCount > 0);
        Assert.False(preview.Valid);
        Assert.Contains(preview.ClockIssues, i => i.Reason == reason);
        var failure = await Assert.ThrowsAsync<ClockConfigurationException>(() => service.ApplyImportAsync(Prefix + addition, TwinImportMode.Append));
        Assert.Contains(failure.Issues, i => i.Reason == reason);
        Assert.Equal(before.Revision, (await ReadAsync()).Revision);
    }

    [Fact]
    public async Task ClockOnlyUpdate_Deletion_AndRollback_Keep35PointsAndLegacyEtag()
    {
        await Materializer.MaterializeAsync(Fixture);
        var first = await Source.ReadAsync("GW-EXAMPLE", default);
        var legacy = PointListEtag.Compute(first.Points);
        var originalRevision = (await ReadAsync()).Revision;
        await Materializer.MaterializeAsync(Fixture.Replace("bos:objectInstance 2", "bos:objectInstance 3"));
        Assert.NotEqual(originalRevision, (await ReadAsync()).Revision);
        Assert.Equal(legacy, PointListEtag.Compute((await Source.ReadAsync("GW-EXAMPLE", default)).Points));
        var clockStart = Fixture.IndexOf("<https://example.com/ri-606/clock/shared-simulation> a bos:ClockSource", StringComparison.Ordinal);
        var noClock = Fixture[..clockStart].Replace("bos:usesClockSource <https://example.com/ri-606/clock/shared-simulation> ;", "");
        // Deleting a referenced definition is refused.
        await Assert.ThrowsAsync<ClockConfigurationException>(() => Materializer.MaterializeAsync(Fixture[..clockStart]));
        await Materializer.MaterializeAsync(noClock);
        var deleted = await ReadAsync();
        Assert.Equal(35, deleted.Points.Length);
        Assert.Empty(deleted.ClockSources);
        Assert.Empty(deleted.ClockBindings);
        await Materializer.MaterializeAsync(Fixture);
        Assert.Equal(originalRevision, (await ReadAsync()).Revision);
    }

    [Fact]
    public async Task LegacyClocklessTwin_AndAppendIntoExistingEquipment_RemainSupported()
    {
        const string legacy = Prefix + "ex:equipment-a a sbco:EquipmentExt ; sbco:id \"e\" ; sbco:hasPoint ex:p . ex:p a sbco:PointExt ; sbco:id \"p\" ; sbco:gatewayId \"GW-EXAMPLE\" .";
        await Materializer.MaterializeAsync(legacy);
        await Materializer.MaterializeAppendAsync(Prefix + "ex:p sbco:unit \"degC\" .");
        var snapshot = await Source.ReadAsync("GW-EXAMPLE", default);
        Assert.Empty(snapshot.ClockSources);
        Assert.Equal("degC", Assert.Single(snapshot.Points).Unit);
    }

    [Fact]
    public async Task ClockEquipmentMustHaveAnIdSoPointBindingsCanBeResolved()
    {
        await Assert.ThrowsAsync<ClockConfigurationException>(() => Materializer.MaterializeAsync(
            Fixture.Replace("sbco:id \"equipment-a\" ;", "")));
    }

    [Fact]
    public async Task ExistingClockCannotBeTransferredToAnotherGateway()
    {
        await Materializer.MaterializeAsync(Fixture);
        var replacement = Fixture.Replace("GW-EXAMPLE", "GW-OTHER");
        var preview = await new OxiGraphTwinAdminService(store.Client, Materializer).PreviewImportAsync(replacement, TwinImportMode.Replace);
        Assert.Contains(preview.ClockIssues, i => i.Reason == "clock_owner_changed");
        await Assert.ThrowsAsync<ClockConfigurationException>(() => Materializer.MaterializeAsync(replacement));
        Assert.Empty((await ReadAsync("GW-OTHER")).Points);
    }

    [Fact]
    public async Task ConcurrentMutationAfterValidation_IsNotOverwritten()
    {
        await Materializer.MaterializeAsync(Fixture);
        var changed = false;
        var handler = new InterceptHandler(async request =>
        {
            var body = await request.Content!.ReadAsStringAsync();
            if (!changed && request.RequestUri!.AbsolutePath.EndsWith("/update") && Uri.UnescapeDataString(body).Replace('+', ' ').Contains("DELETE { ?s ?p ?o }"))
            {
                changed = true;
                await store.Client.ImportTurtleAsync(Prefix + "ex:concurrent sbco:name \"preserve\" .");
            }
        });
        var client = new OxiGraphClient(new HttpClient(handler), store.BaseUrl);
        await Assert.ThrowsAsync<TwinImportConcurrencyException>(() => new OxiGraphIngestMaterializer(client).MaterializeAsync(Fixture));
        Assert.True(changed);
        Assert.Single(await store.Client.QueryAsync("SELECT ?name WHERE { <https://example.com/ri-606/concurrent> <https://www.sbco.or.jp/ont/name> ?name }"));
    }

    private sealed class InterceptHandler(Func<HttpRequestMessage, Task> before) : DelegatingHandler(new HttpClientHandler())
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await before(request);
            return await base.SendAsync(request, ct);
        }
    }
}
