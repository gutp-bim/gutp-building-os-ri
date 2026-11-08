using BuildingOS.IntegrationTest.Common;
using BuildingOS.IntegrationTest.Common.Fixtures;
using BuildingOS.Shared.Domain.TwinAdmin;
using BuildingOS.Shared.Infrastructure.Oss;
using BuildingOS.Shared.Infrastructure.OxiGraph;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BuildingOS.IntegrationTest.Tests;

/// <summary>#637 against a real OxiGraph: the bos:feedbackPoint import check (replace and append) and the resolver.</summary>
public class FeedbackPointTest(OxiGraphFixture oxiGraph)
    : IntegrationTestBase, IClassFixture<OxiGraphFixture>, IAsyncLifetime
{
    public Task InitializeAsync() => oxiGraph.ClearAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private OxiGraphTwinAdminService Service() =>
        new(oxiGraph.Client, new OxiGraphIngestMaterializer(oxiGraph.Client));

    private const string Twin = """
        @prefix sbco: <https://www.sbco.or.jp/ont/> .
        @prefix bos: <http://buildingos.gutp.jp/ontology#> .
        <urn:test:b> a sbco:Building ; sbco:id "B" ; sbco:name "Building" ; sbco:hasPart <urn:test:f> .
        <urn:test:f> a sbco:Level ; sbco:id "F" ; sbco:name "Floor" .
        <urn:test:d> a sbco:EquipmentExt ; sbco:id "D" ; sbco:name "Device" ; sbco:locatedIn <urn:test:f> ;
            sbco:hasPoint <urn:test:sp> , <urn:test:pv> , <urn:test:pv2> .
        <urn:test:sp> a sbco:PointExt ; sbco:id "SP" ; sbco:name "Setpoint" ; sbco:gatewayId "GW" ;
            sbco:writable "true" ; bos:dataType "number" ;
            bos:feedbackPoint <urn:test:pv> ; bos:feedbackWindowSec "30" ; bos:feedbackTolerance "0.5" .
        <urn:test:pv> a sbco:PointExt ; sbco:id "PV" ; sbco:name "Present value" ; sbco:gatewayId "GW" .
        <urn:test:pv2> a sbco:PointExt ; sbco:id "PV2" ; sbco:name "Other value" ; sbco:gatewayId "GW" .
        """;

    [Fact]
    public async Task ASoundDeclaration_IsValid_AndTheResolverReadsIt()
    {
        var ct = CancellationToken.None;
        var preview = await Service().PreviewImportAsync(Twin, TwinImportMode.Replace, ct);
        Assert.True(preview.Valid);
        Assert.Equal(0, preview.FeedbackPointIssueCount);
        await Service().ApplyImportAsync(Twin, TwinImportMode.Replace, ct);

        var config = await new OssControlFeedbackConfigResolver(oxiGraph.Client, NullLogger<OssControlFeedbackConfigResolver>.Instance)
            .ResolveAsync("SP", ct);

        Assert.Equal("PV", config.FeedbackPointId);
        Assert.Equal(TimeSpan.FromSeconds(30), config.Window);
        Assert.Equal(0.5, config.Tolerance);
    }

    [Fact]
    public async Task AnAppendThatAddsASecondTarget_IsRefused()
    {
        var ct = CancellationToken.None;
        await Service().ApplyImportAsync(Twin, TwinImportMode.Replace, ct);

        // Only the new edge is staged; the point and its first target live in the existing twin.
        var preview = await Service().PreviewImportAsync("""
            @prefix bos: <http://buildingos.gutp.jp/ontology#> .
            <urn:test:sp> bos:feedbackPoint <urn:test:pv2> .
            """, TwinImportMode.Append, ct);

        Assert.False(preview.Valid);
        Assert.Equal(FeedbackPointIssueReasons.MultipleTargets, Assert.Single(preview.FeedbackPointIssues).Reason);
    }

    [Fact]
    public async Task AnEdgeOnlyAppend_ToAMissingTarget_IsRefused()
    {
        var ct = CancellationToken.None;
        await Service().ApplyImportAsync(Twin.Replace("bos:feedbackPoint <urn:test:pv> ;", "", StringComparison.Ordinal), TwinImportMode.Replace, ct);

        var preview = await Service().PreviewImportAsync("""
            @prefix bos: <http://buildingos.gutp.jp/ontology#> .
            <urn:test:sp> bos:feedbackPoint <urn:test:missing> .
            """, TwinImportMode.Append, ct);

        Assert.Equal(FeedbackPointIssueReasons.TargetMissing, Assert.Single(preview.FeedbackPointIssues).Reason);
    }

    [Fact]
    public async Task APointWithoutADeclaration_HasNoFeedback()
    {
        await Service().ApplyImportAsync(Twin, TwinImportMode.Replace, CancellationToken.None);

        var config = await new OssControlFeedbackConfigResolver(oxiGraph.Client, NullLogger<OssControlFeedbackConfigResolver>.Instance)
            .ResolveAsync("PV", CancellationToken.None);

        Assert.Null(config.FeedbackPointId);
    }
}
