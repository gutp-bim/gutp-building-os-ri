# S22 single-stream ceiling (.NET load generator) — s22s1106223712 (baseline-serial)

commit cfcd4e3 · host bench-host · ingress localhost:15051 · workers 83647 {"GRPC_INGRESS_PUBLISH_WINDOW": "1", "HOT_STORE_COALESCE_MS": "0", "PARQUET_FLUSH_INTERVAL": "1", "WARM_STORE": "parquet"}

| target/s | achieved/s | ratio | sent | accepted | peak lag | worker CPU% p50/max | worker RSS MB | loadgen CPU% p50 | NATS CPU% p50 | result |
|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|---|
| 1000 | 675.2 | 0.6752 | 50543 | 50543 | 0 | 81.1/162.2 | 823.8 | 5.5 | 52 | FAIL: achieved 67.5% < 95% of target |
| 2000 | 694.7 | 0.3474 | 49108 | 49108 | 171 | 78.1/126.8 | 1336.2 | 1.0 | 52 | FAIL: achieved 34.7% < 95% of target |
