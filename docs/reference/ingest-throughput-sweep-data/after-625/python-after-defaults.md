# S22 ingest rate sweep — s221106222254 (after-defaults)

commit cfcd4e3 · host bench-host · ingress localhost:15051 · workers 73070 {"PARQUET_FLUSH_INTERVAL": "1", "WARM_STORE": "parquet"}

duration/step 60s · points/gateway 100 · cpu 16 cores (load generator shares the host)

| target/s | streams | achieved/s | accepted | lake rows | loss | dup | peak lag | end lag | drain s | worker CPU% p50/max | worker RSS MB | NATS CPU% p50 | loadgen CPU% p50 | per replica (accepted/s) | result |
|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|---|---|
| 5000 | 1 | 2902.4 | 186002 | 186003 | 0.0 | 0.0 | 1 | 0 | 60.8 | 227.5/359.7 | 1660.4 | 119 | 96.1 | localhost:15051: 2902.4 | FAIL: achieved 58.0% < 95% of target |

**Knee:** first failing step = 5000 frames/s
