using System.Net;
using System.Text.Json;
using BuildingOS.Shared.Domain.TwinAdmin;
using BuildingOS.Shared.Infrastructure.OxiGraph;

namespace BuildingOS.Shared.Test.Infrastructure.OxiGraph;

/// <summary>
/// #600: <c>bos:releaseOf</c> detection — the pure classifier, the graph-scoping of its query, and its
/// effect on the admin import preview (a release point that cannot work blocks apply).
/// </summary>
public class ReleaseOfIssueDetectionTest
{
    private const string Sbco = "https://www.sbco.or.jp/ont/";
    private const string Bos = "http://buildingos.gutp.jp/ontology#";
    private const string OnlyOne = "{\"1\":\"release\"}";

    // A well-formed release point and its target; each test breaks exactly one fact.
    private static Dictionary<string, string> Sound() => new()
    {
        ["pt"] = "urn:rel",
        ["target"] = "urn:tgt",
        ["targetIsPoint"] = "true",
        ["targetWritable"] = "true",
        ["targetGw"] = "GW1",
        ["gw"] = "GW1",
        ["writable"] = "true",
        ["dataType"] = "enum",
        ["enumLabels"] = OnlyOne,
    };

    private static string? ReasonOf(Dictionary<string, string> row)
    {
        var (count, issues) = ReleaseOfIssueDetection.Classify([row], cap: 10);
        return count == 0 ? null : Assert.Single(issues).Reason;
    }

    [Fact]
    public void Classify_SoundReleasePoint_HasNoIssue() => Assert.Null(ReasonOf(Sound()));

    [Theory]
    [InlineData("targetIsPoint", ReleaseOfIssueReasons.TargetMissing)]
    [InlineData("targetWritable", ReleaseOfIssueReasons.TargetNotWritable)]
    [InlineData("gw", ReleaseOfIssueReasons.ReleaseNoGateway)]
    [InlineData("targetGw", ReleaseOfIssueReasons.TargetOtherGateway)]
    [InlineData("writable", ReleaseOfIssueReasons.ReleaseNotWritable)]
    [InlineData("dataType", ReleaseOfIssueReasons.SchemaNotReleaseOnly)]
    [InlineData("enumLabels", ReleaseOfIssueReasons.SchemaNotReleaseOnly)]
    public void Classify_MissingFact_IsReportedWithItsReason(string missingKey, string expected)
    {
        var row = Sound();
        row.Remove(missingKey);
        Assert.Equal(expected, ReasonOf(row));
    }

    [Fact]
    public void Classify_TargetOnAnotherGateway_IsReported()
    {
        var row = Sound();
        row["targetGw"] = "GW2";
        Assert.Equal(ReleaseOfIssueReasons.TargetOtherGateway, ReasonOf(row));
    }

    [Fact]
    public void Classify_TargetThatIsItselfARelease_IsReported()
    {
        var row = Sound();
        row["targetReleaseOf"] = "urn:other";
        Assert.Equal(ReleaseOfIssueReasons.TargetIsRelease, ReasonOf(row));
    }

    [Fact]
    public void Classify_SelfReference_IsReported()
    {
        var row = Sound();
        row["target"] = "urn:rel";
        Assert.Equal(ReleaseOfIssueReasons.SelfReference, ReasonOf(row));
    }

    [Fact]
    public void Classify_SeveralTargets_AreOneIssueNotSeveral()
    {
        var a = Sound();
        var b = Sound();
        b["target"] = "urn:tgt2";
        var (count, issues) = ReleaseOfIssueDetection.Classify([a, b], cap: 10);
        Assert.Equal(1, count);
        Assert.Equal(ReleaseOfIssueReasons.MultipleTargets, issues[0].Reason);
    }

    [Fact]
    public void Classify_SameRowRepeatedByTheGraphUnion_IsJudgedOnce()
    {
        var (count, _) = ReleaseOfIssueDetection.Classify([Sound(), Sound()], cap: 10);
        Assert.Equal(0, count);
    }

    [Theory]
    [InlineData("boolean", null)]                       // boolean accepts 0 as well as 1
    [InlineData("number", null)]
    [InlineData("enum", "{\"0\":\"off\",\"1\":\"release\"}")] // 0 would be accepted
    [InlineData("enum", "{\"2\":\"other\"}")]            // 1 is not accepted
    [InlineData("enum", "{}")]                          // empty set is permissive in the validator
    [InlineData("enum", "not json")]
    public void Classify_SchemaThatDoesNotAcceptOnlyOne_IsReported(string dataType, string? enumLabels)
    {
        var row = Sound();
        row["dataType"] = dataType;
        if (enumLabels is null) row.Remove("enumLabels"); else row["enumLabels"] = enumLabels;
        Assert.Equal(ReleaseOfIssueReasons.SchemaNotReleaseOnly, ReasonOf(row));
    }

    [Fact]
    public void Classify_DataTypeSpelling_FollowsTheValidator()
    {
        var row = Sound();
        row["dataType"] = " ENUM ";
        Assert.Null(ReasonOf(row));
    }

    [Fact]
    public void Classify_CapsTheIssueListButCountsAll()
    {
        var rows = Enumerable.Range(0, 5).Select(i =>
        {
            var r = Sound();
            r["pt"] = $"urn:rel{i}";
            r.Remove("targetIsPoint");
            return (IReadOnlyDictionary<string, string>)r;
        }).ToList();

        var (count, issues) = ReleaseOfIssueDetection.Classify(rows, cap: 2);

        Assert.Equal(5, count);
        Assert.Equal(2, issues.Count);
    }

    // ── Query scoping ─────────────────────────────────────────────────────────

    [Fact]
    public void BuildQuery_NoGraph_QueriesTheDefaultGraphPlainly()
    {
        var q = ReleaseOfIssueDetection.BuildQuery(null, null);

        Assert.Contains($"?pt a <{Sbco}PointExt> ; <{Bos}releaseOf> ?target .", q);
        Assert.DoesNotContain("GRAPH", q);
    }

    [Fact]
    public void BuildQuery_Append_ScopesTheCandidateToTheStagingGraph_ButLooksUpAcrossBoth()
    {
        var q = ReleaseOfIssueDetection.BuildQuery("urn:staging", TwinImportMode.Append);

        // Only the release points the import brings in are judged...
        Assert.Contains($"GRAPH <urn:staging> {{ ?pt a <{Sbco}PointExt> ; <{Bos}releaseOf> ?target . }}", q);
        // ...but the target may already be in the twin, so its lookups union both graphs.
        Assert.Contains($"UNION {{ ?target <{Sbco}writable> ?targetWritable . }}", q);
    }

    [Fact]
    public void BuildQuery_Replace_OnlyCountsTheStagedTriples()
    {
        var q = ReleaseOfIssueDetection.BuildQuery("urn:staging", TwinImportMode.Replace);

        Assert.Contains($"GRAPH <urn:staging> {{ ?target <{Sbco}writable> ?targetWritable . }}", q);
        Assert.DoesNotContain("UNION", q);
    }

    // ── Admin import preview ──────────────────────────────────────────────────

    private static OxiGraphTwinAdminService CreateService(IReadOnlyList<Dictionary<string, string>> releaseOfRows)
    {
        var http = new HttpClient(new TwinAdminMockHandler(req =>
        {
            if (req.Method == HttpMethod.Put || req.RequestUri!.AbsolutePath.EndsWith("/update"))
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            var q = Uri.UnescapeDataString(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult()).Replace('+', ' ');
            if (q.Contains("SELECT (COUNT"))
                return Bindings([new() { ["n"] = "0" }]);
            return q.Contains("releaseOf") ? Bindings(releaseOfRows) : Bindings([]);
        })) { BaseAddress = new Uri("http://oxi:7878") };
        var client = new OxiGraphClient(http, "http://oxi:7878");
        return new OxiGraphTwinAdminService(client, new OxiGraphIngestMaterializer(client));
    }

    private static HttpResponseMessage Bindings(IReadOnlyList<Dictionary<string, string>> rows) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            results = new { bindings = rows.Select(r => r.ToDictionary(kv => kv.Key, kv => new { value = kv.Value })) },
        })),
    };

    [Fact]
    public async Task PreviewImport_BrokenReleasePoint_IsReportedAndMakesThePreviewInvalid()
    {
        var broken = Sound();
        broken.Remove("targetIsPoint");

        var preview = await CreateService([broken]).PreviewImportAsync("ttl", TwinImportMode.Append);

        Assert.Equal(1, preview.ReleaseOfIssueCount);
        var issue = Assert.Single(preview.ReleaseOfIssues);
        Assert.Equal("urn:rel", issue.PointId);
        Assert.Equal(ReleaseOfIssueReasons.TargetMissing, issue.Reason);
        Assert.False(preview.Valid);
    }

    [Fact]
    public async Task PreviewImport_SoundReleasePoint_LeavesThePreviewValid()
    {
        var preview = await CreateService([Sound()]).PreviewImportAsync("ttl", TwinImportMode.Append);

        Assert.Equal(0, preview.ReleaseOfIssueCount);
        Assert.Empty(preview.ReleaseOfIssues);
        Assert.True(preview.Valid);
    }
}
