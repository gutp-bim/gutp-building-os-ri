namespace BuildingOS.Shared.Infrastructure.BlobStorage;

public interface IBlobStorage
{
    Task PutAsync(string container, string key, Stream content, string contentType = "application/octet-stream", CancellationToken cancellationToken = default);
    Task<Stream?> GetAsync(string container, string key, CancellationToken cancellationToken = default);
    Task<bool> ExistsAsync(string container, string key, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> ListAsync(string container, string prefix = "", CancellationToken cancellationToken = default);
    Task DeleteAsync(string container, string key, CancellationToken cancellationToken = default);
}
