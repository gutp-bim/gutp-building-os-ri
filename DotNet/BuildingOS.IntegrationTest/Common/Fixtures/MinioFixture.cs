using Amazon.Runtime;
using Amazon.S3;
using Testcontainers.Minio;
using Xunit;

namespace BuildingOS.IntegrationTest.Common.Fixtures;

public class MinioFixture : IAsyncLifetime
{
    private const string AccessKey = "minioadmin";
    private const string SecretKey = "minioadmin";

    private readonly MinioContainer _container = new MinioBuilder()
        .WithUsername(AccessKey)
        .WithPassword(SecretKey)
        .Build();

    public IAmazonS3 S3Client { get; private set; } = null!;

    public IAmazonS3 CreateS3Client() => new AmazonS3Client(
        AccessKey,
        SecretKey,
        new AmazonS3Config
        {
            ServiceURL = _container.GetConnectionString(),
            ForcePathStyle = true,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        });

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        S3Client = new AmazonS3Client(
            AccessKey,
            SecretKey,
            new AmazonS3Config
            {
                ServiceURL = _container.GetConnectionString(),
                ForcePathStyle = true,
                RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
                ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
            });
    }

    public async Task DisposeAsync()
    {
        S3Client?.Dispose();
        await _container.DisposeAsync();
    }
}
