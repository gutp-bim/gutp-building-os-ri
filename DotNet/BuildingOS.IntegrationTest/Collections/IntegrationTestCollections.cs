using BuildingOS.IntegrationTest.Common.Fixtures;
using Xunit;

namespace BuildingOS.IntegrationTest.Collections;

public static class Names
{
    public const string Nats = "Nats";
    public const string Minio = "Minio";
    public const string Postgres = "Postgres";
    public const string PgBouncer = "PgBouncer";
}

[CollectionDefinition(Names.Nats)]
public class NatsCollection : ICollectionFixture<NatsFixture> { }

[CollectionDefinition(Names.Minio)]
public class MinioCollection : ICollectionFixture<MinioFixture> { }

[CollectionDefinition(Names.Postgres)]
public class PostgresCollection : ICollectionFixture<PostgresFixture> { }

[CollectionDefinition(Names.PgBouncer)]
public class PgBouncerCollection : ICollectionFixture<PgBouncerFixture> { }
