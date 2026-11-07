# S22 ingest rate sweep — s221106222047 (a-only)

commit cfcd4e3 · host bench-host · ingress localhost:15051 · workers 71202 {"GRPC_INGRESS_PUBLISH_WINDOW": "1", "PARQUET_FLUSH_INTERVAL": "1", "WARM_STORE": "parquet"}

duration/step 60s · points/gateway 100 · cpu 16 cores (load generator shares the host)

| target/s | streams | achieved/s | accepted | lake rows | loss | dup | peak lag | end lag | drain s | worker CPU% p50/max | worker RSS MB | NATS CPU% p50 | loadgen CPU% p50 | per replica (accepted/s) | result |
|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|---|---|
| 5000 | 1 | 1148.7 | 90338 | 90339 | 0.0 | 0.0 | 1 | 0 | 40.5 | 125.9/229.5 | 1432.5 | 60 | 39.3 | localhost:15051: 1148.7 | FAIL: achieved 23.0% < 95% of target |

**Knee:** first failing step = 5000 frames/s
