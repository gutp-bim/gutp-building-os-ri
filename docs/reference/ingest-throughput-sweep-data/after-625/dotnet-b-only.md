# S22 single-stream ceiling (.NET load generator) — s22s1106224348 (b-only)

commit cfcd4e3 · host Takashi-PC · ingress localhost:15051 · workers 86983 {"HOT_STORE_COALESCE_MS": "0", "PARQUET_FLUSH_INTERVAL": "1", "WARM_STORE": "parquet"}

| target/s | achieved/s | ratio | sent | accepted | peak lag | worker CPU% p50/max | worker RSS MB | loadgen CPU% p50 | NATS CPU% p50 | result |
|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|---|
| 5000 | 4994.5 | 0.9989 | 299917 | 299917 | 28 | 130.4/502.0 | 1919.6 | 42.7 | 64 | PASS |
| 8000 | 7991.8 | 0.999 | 479934 | 479934 | 880 | 211.7/391.7 | 3312.7 | 66.9 | 99 | PASS |
