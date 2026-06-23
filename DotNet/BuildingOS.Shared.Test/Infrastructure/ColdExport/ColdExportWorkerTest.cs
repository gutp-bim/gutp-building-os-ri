using BuildingOS.Shared.Infrastructure.ColdExport;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BuildingOS.Shared.Test.Infrastructure.ColdExport;

public class ColdExportWorkerTest
{
    private readonly Mock<IColdExportService> _service = new();

    [Fact]
    public async Task ExportOnceAsync_Calls_Service_WithCorrectWindow()
    {
        var from = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var to   = new DateTime(2024, 1, 1, 0, 5, 0, DateTimeKind.Utc);

        _service.Setup(s => s.ExportChunkAsync(from, to, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ColdExportResult(100, 4096, "cold/test.parquet", true));

        var worker = CreateWorker();
        await worker.ExportOnceAsync(from, to, CancellationToken.None);

        _service.Verify(s => s.ExportChunkAsync(from, to, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExportOnceAsync_DoesNotThrow_WhenServiceReturnsZeroRows()
    {
        _service.Setup(s => s.ExportChunkAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ColdExportResult(0, 0, null, false));

        var worker = CreateWorker();
        var ex = await Record.ExceptionAsync(() =>
            worker.ExportOnceAsync(DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow, CancellationToken.None));

        Assert.Null(ex);
    }

    [Fact]
    public async Task ExportOnceAsync_DoesNotThrow_WhenServiceThrows()
    {
        _service.Setup(s => s.ExportChunkAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("connection refused"));

        var worker = CreateWorker();
        var ex = await Record.ExceptionAsync(() =>
            worker.ExportOnceAsync(DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow, CancellationToken.None));

        Assert.Null(ex);
    }

    private ColdExportWorker CreateWorker() =>
        new(_service.Object, NullLogger<ColdExportWorker>.Instance, intervalMinutes: 5);
}
