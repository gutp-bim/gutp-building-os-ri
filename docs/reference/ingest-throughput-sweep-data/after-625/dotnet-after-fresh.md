# S22 single-stream ceiling (.NET load generator) — s22s1106224557 (after-fresh)

commit cfcd4e3 · host bench-host · ingress localhost:15051 · workers 88246 {"PARQUET_FLUSH_INTERVAL": "1", "WARM_STORE": "parquet"}

| target/s | achieved/s | ratio | sent | accepted | peak lag | worker CPU% p50/max | worker RSS MB | loadgen CPU% p50 | NATS CPU% p50 | result |
|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|---|
| 12000 | 11988.7 | 0.9991 | 719981 | 719981 | 13105 | 238.0/456.2 | 2605.7 | 102.1 | 110 | PASS |
