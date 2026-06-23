using BuildingOS.Shared.Infrastructure.BlobStorage;
using BuildingOS.Shared.Infrastructure.ColdExport;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BuildingOS.Shared.Test.Infrastructure.ColdExport;

public class NpgsqlMinioExportServiceTest
{
    private readonly Mock<IExportDataReader> _reader = new();
    private readonly Mock<IBlobStorage> _storage = new();
    private readonly Mock<IExportLogRepository> _log = new();

    private NpgsqlMinioExportService CreateSut() =>
        new(_reader.Object, _storage.Object, _log.Object, NullLogger<NpgsqlMinioExportService>.Instance);

    [Fact]
    public async Task ExportChunkAsync_Returns_ZeroRows_WhenNoData()
    {
        var from = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var to   = new DateTime(2024, 1, 1, 0, 5, 0, DateTimeKind.Utc);

        _reader.Setup(r => r.ReadAsync(from, to, It.IsAny<CancellationToken>()))
               .ReturnsAsync(Array.Empty<ValidTelemetryData>());

        var result = await CreateSut().ExportChunkAsync(from, to);

        Assert.Equal(0, result.RowsExported);
        Assert.Null(result.ParquetPath);
        _storage.Verify(s => s.PutAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Stream>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExportChunkAsync_UploadsParquetToStorage_WhenDataExists()
    {
        var from = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var to   = new DateTime(2024, 1, 1, 0, 5, 0, DateTimeKind.Utc);
        var rows = new[] { new ValidTelemetryData { PointId = "p1", Value = 22.5, Datetime = from.ToString("O"), Building = "B1" } };

        _reader.Setup(r => r.ReadAsync(from, to, It.IsAny<CancellationToken>())).ReturnsAsync(rows);
        _log.Setup(l => l.InsertAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<string>(),
            It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var result = await CreateSut().ExportChunkAsync(from, to);

        Assert.Equal(1, result.RowsExported);
        Assert.NotNull(result.ParquetPath);
        _storage.Verify(s => s.PutAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Stream>(),
            "application/octet-stream", It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task ExportChunkAsync_SetsVerified_AfterSuccessfulUpload()
    {
        var from = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var to   = new DateTime(2024, 1, 1, 0, 5, 0, DateTimeKind.Utc);
        var rows = new[] { new ValidTelemetryData { PointId = "p1", Value = 1.0, Datetime = from.ToString("O"), Building = "B1" } };

        _reader.Setup(r => r.ReadAsync(from, to, It.IsAny<CancellationToken>())).ReturnsAsync(rows);
        _log.Setup(l => l.InsertAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<string>(),
            It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>())).ReturnsAsync(42);

        var result = await CreateSut().ExportChunkAsync(from, to);

        Assert.True(result.Verified);
        _log.Verify(l => l.SetVerifiedAsync(42, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExportChunkAsync_Partitions_ParquetPath_By_Building_And_Hour()
    {
        var from = new DateTime(2024, 1, 15, 9, 0, 0, DateTimeKind.Utc);
        var to   = new DateTime(2024, 1, 15, 9, 5, 0, DateTimeKind.Utc);
        var rows = new[] { new ValidTelemetryData { PointId = "p1", Value = 1.0, Datetime = from.ToString("O"), Building = "building-A" } };

        _reader.Setup(r => r.ReadAsync(from, to, It.IsAny<CancellationToken>())).ReturnsAsync(rows);
        _log.Setup(l => l.InsertAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<string>(),
            It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>())).ReturnsAsync(1);

        string? capturedKey = null;
        _storage.Setup(s => s.PutAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Stream>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, Stream, string, CancellationToken>((_, key, _, _, _) => capturedKey = key)
            .Returns(Task.CompletedTask);

        await CreateSut().ExportChunkAsync(from, to);

        Assert.NotNull(capturedKey);
        Assert.Contains("building_id=building-A", capturedKey);
        Assert.Contains("year=2024", capturedKey);
        Assert.Contains("month=01", capturedKey);
        Assert.Contains("day=15", capturedKey);
        Assert.Contains("hour=09", capturedKey);
    }
}
