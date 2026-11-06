# S22 ingest rate sweep — s221106222503 (after-s3)

commit cfcd4e3 · host Takashi-PC · ingress localhost:15051 · workers 73070 {"PARQUET_FLUSH_INTERVAL": "1", "WARM_STORE": "parquet"}

duration/step 60s · points/gateway 100 · cpu 16 cores (load generator shares the host)

| target/s | streams | achieved/s | accepted | lake rows | loss | dup | peak lag | end lag | drain s | worker CPU% p50/max | worker RSS MB | NATS CPU% p50 | loadgen CPU% p50 | per replica (accepted/s) | result |
|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|---|---|
| 8000 | 3 | 7302.6 | 454042 | 454045 | 0.0 | 0.0 | 8626 | 2531 | 60.9 | 345.7/416.3 | 2925.6 | 140 | 272.5 | localhost:15051: 7302.6 | FAIL: achieved 91.3% < 95% of target |

**Knee:** first failing step = 8000 frames/s
