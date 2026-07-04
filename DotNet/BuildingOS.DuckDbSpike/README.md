# BuildingOS.DuckDbSpike — Experimental

> **This project is a research spike and is NOT production code.**
> It is kept in the solution for benchmark reproducibility only.
> Do not depend on it from other projects.

Benchmarks DuckDB-WASM as an alternative read engine for the Parquet lake (issue #221).
Evaluation criteria and benchmark results are in [`results/README.md`](results/README.md).

Adoption decision is pending benchmark results; the current production path is `ParquetLakeTelemetryStore` (Parquet.Net + MinIO).
