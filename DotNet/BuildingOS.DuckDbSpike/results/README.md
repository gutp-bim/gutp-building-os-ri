# DuckDB Spike Results (#221)

Benchmark results from `dotnet run` are stored here as `run-{YYYYMMDD-HHmm}.txt`.

## How to run

```bash
cd DotNet
dotnet run --project BuildingOS.DuckDbSpike -- \
  --minio http://localhost:9000 \
  --access-key buildingos --secret-key buildingos123 \
  --bucket lake --building B01 --point P001 \
  --start 2025-11-01T00:00:00Z --end 2025-11-01T23:59:59Z \
  --iterations 3 \
  | tee results/run-$(date +%Y%m%d-%H%M).txt
```

## Evaluation criteria

| Axis | Parquet.Net baseline | DuckDB target | Adopt? |
|---|---|---|---|
| Warm 24h/1point p95 | < 2 s | < 2 s | N/A |
| Cold 7d/1point p95 | < 5 s | < 5 s | ✓ if ≥ 30% faster |
| Multi-point 7d p95 | — | — | ✓ if ≥ 50% faster |
| Memory peak | — | ≤ Parquet.Net | required |
| Native binary size | 0 (managed) | ~120 MB | accept if perf justifies |
| Cold start latency | < 100 ms | < 200 ms | accept |

## Results

_No runs yet. Run the spike with a real MinIO instance and document results here._

See `docs/oss-duckdb-spike.md` for the full evaluation methodology and adoption criteria.
