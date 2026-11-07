# S22 ingest rate sweep — s221106220955 (baseline-serial)

commit f68d088 · host bench-host · ingress localhost:15051 · workers 63365 {"GRPC_INGRESS_PUBLISH_WINDOW": "1", "HOT_STORE_COALESCE_MS": "0", "PARQUET_FLUSH_INTERVAL": "1", "WARM_STORE": "parquet"}

duration/step 60s · points/gateway 100 · cpu 16 cores (load generator shares the host)

| target/s | streams | achieved/s | accepted | lake rows | loss | dup | peak lag | end lag | drain s | worker CPU% p50/max | worker RSS MB | NATS CPU% p50 | loadgen CPU% p50 | per replica (accepted/s) | result |
|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|---|---|
| 2000 | 1 | 632.9 | 77319 | 77320 | 0.0 | 0.0 | 1 | 0 | 60.7 | 85.1/192.5 | 1345.4 | 51 | 9.9 | localhost:15051: 632.9 | FAIL: achieved 31.6% < 95% of target |
| 5000 | 1 | 637.0 | 215443 | 215444 | 0.0 | 0.0 | 165 | 0 | 40.4 | 81.1/171.6 | 2224.2 | 51 | 10.5 | localhost:15051: 637.0 | FAIL: achieved 12.7% < 95% of target |

**Knee:** first failing step = 2000 frames/s
