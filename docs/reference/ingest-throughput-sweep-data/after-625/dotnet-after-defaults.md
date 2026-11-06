# S22 single-stream ceiling (.NET load generator) — s22s1106223235 (after-defaults)

commit cfcd4e3 · host Takashi-PC · ingress localhost:15051 · workers 73070 {"PARQUET_FLUSH_INTERVAL": "1", "WARM_STORE": "parquet"}

| target/s | achieved/s | ratio | sent | accepted | peak lag | worker CPU% p50/max | worker RSS MB | loadgen CPU% p50 | NATS CPU% p50 | result |
|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|---|
| 2000 | 1997.8 | 0.9989 | 119973 | 119973 | 0 | 45.3/114.0 | 3257.5 | 20.4 | 23 | PASS |
| 5000 | 4994.4 | 0.9989 | 299989 | 299989 | 0 | 94.1/222.5 | 3572.4 | 41.9 | 49 | PASS |
| 8000 | 7991.8 | 0.999 | 479997 | 479997 | 286 | 140.9/220.0 | 3582.7 | 66.1 | 70 | PASS |
| 12000 | 11985.9 | 0.9988 | 719787 | 719787 | 1673 | 213.5/357.0 | 3612.7 | 105.0 | 102 | PASS |
