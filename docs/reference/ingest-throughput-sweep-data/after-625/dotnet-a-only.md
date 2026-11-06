# S22 single-stream ceiling (.NET load generator) — s22s1106223946 (a-only)

commit cfcd4e3 · host Takashi-PC · ingress localhost:15051 · workers 85035 {"GRPC_INGRESS_PUBLISH_WINDOW": "1", "PARQUET_FLUSH_INTERVAL": "1", "WARM_STORE": "parquet"}

| target/s | achieved/s | ratio | sent | accepted | peak lag | worker CPU% p50/max | worker RSS MB | loadgen CPU% p50 | NATS CPU% p50 | result |
|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|---|
| 2000 | 1207.5 | 0.6038 | 97627 | 97627 | 0 | 100.9/205.4 | 1117.0 | 14.4 | 60 | FAIL: achieved 60.4% < 95% of target |
| 5000 | 1233.2 | 0.2466 | 189492 | 189492 | 227 | 97.5/165.7 | 2103.7 | 13.9 | 60 | FAIL: achieved 24.7% < 95% of target |
