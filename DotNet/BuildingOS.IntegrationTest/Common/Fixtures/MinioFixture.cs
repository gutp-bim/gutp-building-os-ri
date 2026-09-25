using Amazon.Runtime;
using Amazon.S3;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace BuildingOS.IntegrationTest.Common.Fixtures;

// Backed by RustFS (rustfs/rustfs), not MinIO — minio/minio was delisted from Docker Hub and
// quay.io, so `Testcontainers.Minio`'s MinioBuilder (which pulls minio/minio) can no longer start.
// Mirrors the same image/env/health config as building-os.minio in docker-compose.oss.yaml (#490).
public class MinioFixture : IAsyncLifetime
{
    private const string AccessKey = "minioadmin";
    private const string SecretKey = "minioadmin";
    private const int S3Port = 9000;

    private readonly IContainer _container = new ContainerBuilder()
        .WithImage("rustfs/rustfs:1.0.0")
        .WithEnvironment("RUSTFS_VOLUMES", "/data")
        .WithEnvironment("RUSTFS_ADDRESS", $"0.0.0.0:{S3Port}")
        .WithEnvironment("RUSTFS_ACCESS_KEY", AccessKey)
        .WithEnvironment("RUSTFS_SECRET_KEY", SecretKey)
        .WithPortBinding(S3Port, true)
        .WithWaitStrategy(Wait.ForUnixContainer()
            .UntilHttpRequestIsSucceeded(r => r.ForPort(S3Port).ForPath("/health")))
        .Build();

    public IAmazonS3 S3Client { get; private set; } = null!;

    private string ConnectionString =>
        $"http://{_container.Hostname}:{_container.GetMappedPublicPort(S3Port)}";

    public IAmazonS3 CreateS3Client() => new AmazonS3Client(
        AccessKey,
        SecretKey,
        new AmazonS3Config
        {
            ServiceURL = ConnectionString,
            ForcePathStyle = true,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        });

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        S3Client = CreateS3Client();
    }

    public async Task DisposeAsync()
    {
        S3Client?.Dispose();
        await _container.DisposeAsync();
    }
}
