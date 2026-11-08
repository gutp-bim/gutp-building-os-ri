using BuildingOS.IntegrationTest.Collections;
using BuildingOS.IntegrationTest.Common;
using BuildingOS.IntegrationTest.Common.Fixtures;
using Xunit;

namespace BuildingOS.IntegrationTest.Tests;

[Collection(Names.Postgres)]
public class PostgresConnectionTest(PostgresFixture fixture) : IntegrationTestBase
{
    [Fact]
    public async Task Can_Connect_And_Query()
    {
        await using var conn = await fixture.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1";
        var result = await cmd.ExecuteScalarAsync();
        Assert.Equal(1, result);
    }
}
