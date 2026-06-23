namespace BuildingOS.Shared.Infrastructure.ColdExport;

public record ColdExportResult(
    long RowsExported,
    long BytesWritten,
    string? ParquetPath,
    bool Verified);
