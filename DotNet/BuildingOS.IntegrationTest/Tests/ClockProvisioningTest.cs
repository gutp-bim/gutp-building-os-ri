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

    /// <summary>
    /// Builder #44's pinned artifact, through real RI preview/import/materializer and provisioning.
    /// Reuses this class's disposable OxiGraph fixture; no running stack or API host is used.
    /// </summary>
    [Fact]
    public async Task BuilderGeneratedRdf_ImportsAndExportsMatching35PointSnapshot()
    {
        const string gatewayId = "GW-BOPTEST-001";
        const string riBase = "9b643391d7273de4d92166d2474de1e5e0ea9f83";
        const string builderBase = "f2f313e044b85f7ef127ef95351afef81afdcbc9";
        var input = Path.Combine(AppContext.BaseDirectory, "BuilderClockFixtures");
        var output = Path.Combine(AppContext.BaseDirectory, "BuilderClockArtifacts");
        Directory.CreateDirectory(output);
        // A failed rerun must not leave the previous run's successful evidence looking current.
        foreach (var name in new[] { "provisioning.json", "checks.json" }) File.Delete(Path.Combine(output, name));
        var inputHashes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["model.ttl"] = "86098affbeb550b0e51a1e8268370ede94120ad45eb32c999a19f9c0eb117c87",
            ["pointlist-v2.csv"] = "0813b8d3f60a24d8ba2459c4dd169225f9d50d3810bcf17e31ce9febabcd338e",
            ["clocks.json"] = "356c5213574c234c855269a5c3128ad4eaec4793c813e578c38e4b34b73d6816",
        };
        // Keep the tracked CSV in repository LF format, then restore the supplied artifact's
        // CRLF bytes before hashing/parsing. No fields are normalized or changed.
        var fixtureCsvBytes = await File.ReadAllBytesAsync(Path.Combine(input, "pointlist-v2.csv"));
        var fixtureCsv = System.Text.Encoding.UTF8.GetString(fixtureCsvBytes);
        Assert.DoesNotContain("\r", fixtureCsv);
        var originalCsv = fixtureCsv.Replace("\n", "\r\n", StringComparison.Ordinal);
        foreach (var (name, hash) in inputHashes)
        {
            var bytes = name == "pointlist-v2.csv" ? System.Text.Encoding.UTF8.GetBytes(originalCsv)
                : await File.ReadAllBytesAsync(Path.Combine(input, name));
            Assert.Equal(hash, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant());
        }

        var expectedRows = new List<Dictionary<string, string>>();
        using (var csv = new Microsoft.VisualBasic.FileIO.TextFieldParser(new StringReader(originalCsv)))
        {
            csv.SetDelimiters(",");
            csv.HasFieldsEnclosedInQuotes = true;
            csv.TrimWhiteSpace = false;
            var headers = csv.ReadFields()!;
            while (!csv.EndOfData)
            {
                var fields = csv.ReadFields()!;
                Assert.Equal(headers.Length, fields.Length);
                expectedRows.Add(headers.Select((key, i) => (key, value: fields[i]))
                    .ToDictionary(pair => pair.key, pair => pair.value, StringComparer.Ordinal));
            }
        }
        Assert.Equal(35, expectedRows.Count);
        var expectedById = expectedRows.ToDictionary(r => r["point_id"], StringComparer.Ordinal);
        var ids = Enumerable.Range(2, 35).Select(n => $"BPT-{n:0000}").ToArray();
        Assert.Equal(ids, expectedById.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.All(expectedRows, r => Assert.Equal(gatewayId, r["gateway_id"]));
        var buildingId = Assert.Single(expectedRows.Select(r => r["building_id"]).Distinct());
        var deviceId = Assert.Single(expectedRows.Select(r => r["device_id"]).Distinct());
        using var sidecar = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(input, "clocks.json")));
        var expectedClock = Assert.Single(sidecar.RootElement.GetProperty("clockSources").EnumerateArray());
        var expectedBinding = Assert.Single(sidecar.RootElement.GetProperty("clockBindings").EnumerateArray());
        var expectedNative = expectedClock.GetProperty("native");
        Assert.Equal(deviceId, expectedBinding.GetProperty("equipmentId").GetString());

        var turtle = await File.ReadAllTextAsync(Path.Combine(input, "model.ttl"));
        var service = new OxiGraphTwinAdminService(store.Client, Materializer);
        var preview = await service.PreviewImportAsync(turtle, TwinImportMode.Replace);
        Assert.True(preview.Valid, JsonSerializer.Serialize(preview));
        Assert.Equal(0, preview.ClockIssueCount);
        Assert.Equal(0, preview.OrphanCount);
        await service.ApplyImportAsync(turtle, TwinImportMode.Replace);

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var database = new OxiGraphDigitalTwinDatabase(store.Client, cache);
        var building = Assert.Single(await database.ListBuildings());
        Assert.Equal(buildingId, building.Id);
        var equipment = Assert.Single(await database.ListDevices(null));
        Assert.Equal(deviceId, equipment.Id);
        var snapshot = await Source.ReadAsync(gatewayId, default);
        Assert.Equal(35, snapshot.Points.Length);
        Assert.Equal(ids, snapshot.Points.Select(p => p.PointId).ToArray());
        var clock = Assert.Single(snapshot.ClockSources);
        var binding = Assert.Single(snapshot.ClockBindings);
        Assert.Equal(expectedClock.GetProperty("clockId").GetString(), clock.ClockId);
        Assert.Equal(expectedClock.GetProperty("clockDomain").GetString(), clock.ClockDomain);
        Assert.Equal(expectedClock.GetProperty("unit").GetString(), clock.Unit);
        Assert.Equal(expectedClock.GetProperty("readOnly").GetBoolean(), clock.ReadOnly);
        Assert.Equal(expectedClock.GetProperty("acquisitionMode").GetString(), clock.AcquisitionMode);
        Assert.Equal(expectedBinding.GetProperty("clockId").GetString(), binding.ClockId);
        Assert.Equal(equipment.DtId, binding.EquipmentId);
        Assert.Equal(expectedNative.GetProperty("gatewayId").GetString(), clock.Native.GatewayId);
        Assert.Equal(expectedNative.GetProperty("connectorId").GetString(), clock.Native.ConnectorId);
        Assert.Equal(expectedNative.GetProperty("protocol").GetString(), clock.Native.Protocol);
        Assert.Equal(expectedNative.GetProperty("deviceInstance").GetInt32(), clock.Native.DeviceInstance);
        Assert.Equal(expectedNative.GetProperty("objectType").GetString(), clock.Native.ObjectType);
        Assert.Equal(expectedNative.GetProperty("objectInstance").GetInt32(), clock.Native.ObjectInstance);
        Assert.Equal(expectedNative.GetProperty("property").GetString(), clock.Native.Property);
        Assert.Equal(expectedNative.GetProperty("unit").GetString(), clock.Native.Unit);
        foreach (var point in snapshot.Points)
        {
            var row = expectedById[point.PointId];
            Assert.Equal(row["device_id_bacnet"], point.BacnetDeviceId);
            Assert.Equal(row["object_type_bacnet"], point.BacnetObjectType);
            Assert.Equal(row["instance_no_bacnet"], point.BacnetInstanceNo);
            Assert.Equal(row["protocol"], point.Protocol);
            Assert.Equal(row["local_id"], point.LocalId);
            // Builder omits an empty optional CSV unit; RI represents the absent attribute as null.
            Assert.Equal(string.IsNullOrEmpty(row["unit"]) ? null : row["unit"], point.Unit);
            Assert.Equal(bool.Parse(row["writable"]), point.Writable);
            Assert.Equal(equipment.DtId, point.DeviceDtId);
        }

        const string prefixes = "PREFIX sbco: <https://www.sbco.or.jp/ont/> PREFIX bos: <http://buildingos.gutp.jp/ontology#> ";
        var graphPoints = await store.Client.QueryAsync(prefixes + """
            SELECT ?id ?building ?protocol ?device ?object ?instance WHERE {
              ?p a sbco:PointExt ; sbco:id ?id ; sbco:building ?building ; bos:protocol ?protocol ;
                 sbco:deviceIdBacnet ?device ; sbco:objectTypeBacnet ?object ; sbco:instanceNoBacnet ?instance .
            } ORDER BY ?id
            """);
        Assert.Equal(35, graphPoints.Count);
        foreach (var point in graphPoints)
        {
            var row = expectedById[point["id"]];
            Assert.Equal(building.Id, point["building"]);
            Assert.Equal(row["building"], point["building"]);
            Assert.Equal(row["building_id"], point["building"]);
            Assert.Equal(row["protocol"], point["protocol"]); // Explicit bos:protocol, not fallback inference.
            Assert.Equal(row["device_id_bacnet"], point["device"]);
            Assert.Equal(row["object_type_bacnet"], point["object"]);
            Assert.Equal(row["instance_no_bacnet"], point["instance"]);
        }
        var graphClocks = await store.Client.QueryAsync(prefixes + """
            SELECT ?clock ?native WHERE { ?clock a bos:ClockSource ; bos:nativeBinding ?native . }
            """);
        var graphClock = Assert.Single(graphClocks);
        Assert.Equal(clock.ClockId, graphClock["clock"]);
        Assert.Equal(expectedNative.GetProperty("bindingId").GetString(), graphClock["native"]);
        var graphBindings = await store.Client.QueryAsync(prefixes +
            "SELECT ?equipment ?clock WHERE { ?equipment bos:usesClockSource ?clock . }");
        var graphBinding = Assert.Single(graphBindings);
        Assert.Equal(equipment.DtId, graphBinding["equipment"]);
        Assert.Equal(clock.ClockId, graphBinding["clock"]);
        var metadata = await new BuildingOS.Shared.Module.Oss.OxiGraphPointMetadataDataSource(store.Client).GetAllAsync();
        Assert.Equal(35, metadata.Length);
        Assert.All(metadata, p => { Assert.True(p.HasBuildingPath); Assert.Equal(building.Id, p.Building); });

        var points = await database.ListPoints(null);
        var search = await database.SearchResources(null, "point", null, [], 100, 0);
        var legacy = await database.ListGatewayPointList(gatewayId);
        Assert.Equal(ids, points.Select(p => p.Id).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(ids, search.Select(p => p.Id).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(ids, legacy.Select(p => p.PointId).ToArray());
        Assert.Null(await database.GetPoint(clock.ClockId));
        Assert.DoesNotContain(points, p => p.DtId == clock.ClockId);
        Assert.DoesNotContain(search, p => p.DtId == clock.ClockId);
        Assert.DoesNotContain(legacy, p => p.PointId == clock.ClockId || p.PointId == "BPT-0001");
        Assert.DoesNotContain(legacy, p => p.BacnetDeviceId == clock.Native.DeviceInstance.ToString(System.Globalization.CultureInfo.InvariantCulture)
            && p.BacnetObjectType == clock.Native.ObjectType
            && p.BacnetInstanceNo == clock.Native.ObjectInstance.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(PointListEtag.Compute(legacy), PointListEtag.Compute(snapshot.Points));

        // Direct real controller/source/DTO invocation. No ASP.NET listener or gateway API HTTP request.
        var controller = new BuildingOs.ApiServer.Controllers.GatewayConfigurationController(Source,
            new HeaderGatewayIdentityResolver(), Microsoft.Extensions.Logging.Abstractions.NullLogger<BuildingOs.ApiServer.Controllers.GatewayConfigurationController>.Instance)
        { ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() } };
        controller.Request.Headers["X-Gateway-Id"] = gatewayId;
        var result = Assert.IsType<Microsoft.AspNetCore.Mvc.JsonResult>(await controller.Get(gatewayId, null, default));
        var response = Assert.IsType<GatewayConfigurationResponse>(result.Value);
        var wireOptions = Assert.IsType<JsonSerializerOptions>(result.SerializerSettings);
        Assert.Equal(response.Revision, controller.Response.Headers.ETag.ToString());
        var countBefore = await store.Client.QueryAsync("SELECT (COUNT(*) AS ?count) WHERE { ?s ?p ?o }");
        var previewAgain = await service.PreviewImportAsync(turtle, TwinImportMode.Replace);
        Assert.True(previewAgain.Valid);
        await service.ApplyImportAsync(turtle, TwinImportMode.Replace);
        var after = await ReadAsync(gatewayId);
        var countAfter = await store.Client.QueryAsync("SELECT (COUNT(*) AS ?count) WHERE { ?s ?p ?o }");
        Assert.Equal(response.Revision, after.Revision);
        Assert.Equal(countBefore[0]["count"], countAfter[0]["count"]);
        Assert.Equal(PointListEtag.Compute(legacy), PointListEtag.Compute(await database.ListGatewayPointList(gatewayId)));

        // Emit evidence only after every check has passed. Build output is ignored by Git; the
        // reviewed response/checks are copied into the design report after the fresh build/test run.
        var outputOptions = new JsonSerializerOptions(wireOptions) { WriteIndented = true };
        var responseBytes = JsonSerializer.SerializeToUtf8Bytes(response, outputOptions);
        await File.WriteAllBytesAsync(Path.Combine(output, "provisioning.json"), responseBytes);
        var evidence = new
        {
            status = "passed", riBaselineSha = riBase, builderBaselineSha = builderBase,
            test = nameof(BuilderGeneratedRdf_ImportsAndExportsMatching35PointSnapshot),
            validationLevel = "real import/materializer + source/DTO + direct controller action",
            gatewayApiHttpValidated = false, oxigraphHttpValidated = true,
            isolatedContainer = true, containerImage = "ghcr.io/oxigraph/oxigraph:0.5.0",
            gatewayId, inputSha256 = inputHashes,
            fixtureCsvLineEndings = "LF", csvRestoredToOriginalCrlf = true,
            fixtureCsvSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fixtureCsvBytes)).ToLowerInvariant(),
            pointCount = snapshot.Points.Length,
            clockSourceCount = graphClocks.Count, equipmentClockBindingCount = graphBindings.Count,
            buildingId = building.Id, explicitPointProtocolsPreserved = true,
            pointIdsAndBacnetAttributesPreserved = true, pointBuildingReferencesMatch = true,
            ingressMetadataBuildingMatches = true,
            emptyCsvUnitsRepresentedAsAbsent = expectedRows.Where(r => string.IsNullOrEmpty(r["unit"]))
                .Select(r => r["point_id"]).Order(StringComparer.Ordinal).ToArray(),
            normalPointCount = points.Length,
            pointSearchCount = search.Length, legacyPointListCount = legacy.Length,
            clockExcludedFromBusinessPoints = true, reimportIdempotent = true,
            defaultGraphTripleCount = int.Parse(countBefore[0]["count"], System.Globalization.CultureInfo.InvariantCulture),
            revision = response.Revision,
            provisioningJsonSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(responseBytes)).ToLowerInvariant(),
            preview, reimportPreview = previewAgain,
        };
        await File.WriteAllTextAsync(Path.Combine(output, "checks.json"), JsonSerializer.Serialize(evidence, outputOptions));
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
