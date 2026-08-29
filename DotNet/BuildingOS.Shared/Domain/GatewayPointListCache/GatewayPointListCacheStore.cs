using System.Text.Json;
using BuildingOS.Shared.Domain.Grouping;
using Microsoft.EntityFrameworkCore;

namespace BuildingOS.Shared.Domain.GatewayPointListCache;

/// <summary>EF Core-backed <see cref="IGatewayPointListCacheStore"/> over the
/// <c>gateway_pointlist_cache</c> table (point-list-projection plan, Phase B). Same shape as
/// <see cref="Configuration.SystemConfigStore"/> — a thin repository, no raw SQL.</summary>
public sealed class GatewayPointListCacheStore : IGatewayPointListCacheStore
{
    private readonly RelationalDbContext _context;

    public GatewayPointListCacheStore(RelationalDbContext context)
    {
        _context = context;
    }

    public async Task<GatewayPointListCacheEntry?> GetAsync(string gatewayId, CancellationToken ct = default)
        => await _context.GatewayPointListCacheEntries
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.GatewayId == gatewayId, ct)
            .ConfigureAwait(false);

    public async Task UpsertAsync(
        string gatewayId, string etag, IReadOnlyList<GatewayPointEntry> entries, CancellationToken ct = default)
    {
        var payloadJson = JsonSerializer.Serialize(entries);
        var existing = await _context.GatewayPointListCacheEntries
            .FirstOrDefaultAsync(e => e.GatewayId == gatewayId, ct)
            .ConfigureAwait(false);

        if (existing is null)
        {
            _context.GatewayPointListCacheEntries.Add(new GatewayPointListCacheEntry
            {
                GatewayId = gatewayId,
                Etag = etag,
                PayloadJson = payloadJson,
                PointCount = entries.Count,
                MaterializedAt = DateTime.UtcNow,
            });
        }
        else
        {
            existing.Etag = etag;
            existing.PayloadJson = payloadJson;
            existing.PointCount = entries.Count;
            existing.MaterializedAt = DateTime.UtcNow;
        }

        await _context.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> DeleteAsync(string gatewayId, CancellationToken ct = default)
    {
        var existing = await _context.GatewayPointListCacheEntries
            .FirstOrDefaultAsync(e => e.GatewayId == gatewayId, ct)
            .ConfigureAwait(false);
        if (existing is null) return false;

        _context.GatewayPointListCacheEntries.Remove(existing);
        await _context.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }
}
