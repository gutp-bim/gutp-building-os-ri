using BuildingOS.Shared.Domain.TwinAdmin;

namespace BuildingOS.Shared.Test.Domain.TwinAdmin;

public class SparqlReadOnlyGuardTest
{
    [Theory]
    [InlineData("SELECT ?s WHERE { ?s ?p ?o }")]
    [InlineData("select * where { ?s ?p ?o } limit 10")]
    [InlineData("ASK { ?s ?p ?o }")]
    [InlineData("PREFIX sbco: <https://www.sbco.or.jp/ont/>\nSELECT ?p WHERE { ?p a sbco:PointExt }")]
    [InlineData("  \n SELECT ?x WHERE { ?x ?y ?z }")]
    [InlineData("# a comment\nSELECT ?s WHERE { ?s ?p ?o }")]
    public void Allows_ReadOnlyQueries(string q)
    {
        Assert.True(SparqlReadOnlyGuard.IsReadOnly(q));
    }

    [Theory]
    [InlineData("INSERT DATA { <a> <b> <c> }")]
    [InlineData("DELETE WHERE { ?s ?p ?o }")]
    [InlineData("DROP ALL")]
    [InlineData("DROP GRAPH <urn:x>")]
    [InlineData("CLEAR ALL")]
    [InlineData("LOAD <http://example.org/data.ttl>")]
    [InlineData("CREATE GRAPH <urn:x>")]
    [InlineData("PREFIX x: <urn:x>\nINSERT { ?s ?p ?o } WHERE { ?s ?p ?o }")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("CONSTRUCT { ?s ?p ?o } WHERE { ?s ?p ?o }")] // read-only but not SELECT/ASK → rejected per PRD
    [InlineData("DESCRIBE <urn:x>")]
    public void Rejects_NonReadOnlyOrDisallowed(string q)
    {
        Assert.False(SparqlReadOnlyGuard.IsReadOnly(q));
    }

    [Fact]
    public void Rejects_SelectThatHidesAnUpdateKeyword()
    {
        // A SELECT form that nonetheless contains an update keyword token is refused (defense in depth).
        var (allowed, reason) = SparqlReadOnlyGuard.Validate("SELECT ?s WHERE { ?s ?p ?o } ; DROP ALL");
        Assert.False(allowed);
        Assert.Contains("DROP", reason);
    }

    [Fact]
    public void Validate_GivesReason_ForUpdateForm()
    {
        var (allowed, reason) = SparqlReadOnlyGuard.Validate("INSERT DATA { <a> <b> <c> }");
        Assert.False(allowed);
        Assert.NotNull(reason);
    }
}
