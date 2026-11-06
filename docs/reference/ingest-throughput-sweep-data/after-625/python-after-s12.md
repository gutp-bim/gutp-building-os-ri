# S22 ingest rate sweep — s221106222712 (after-s12)

commit cfcd4e3 · host Takashi-PC · ingress localhost:15051 · workers 73070 {"PARQUET_FLUSH_INTERVAL": "1", "WARM_STORE": "parquet"}

duration/step 60s · points/gateway 100 · cpu 16 cores (load generator shares the host)

| target/s | streams | achieved/s | accepted | lake rows | loss | dup | peak lag | end lag | drain s | worker CPU% p50/max | worker RSS MB | NATS CPU% p50 | loadgen CPU% p50 | per replica (accepted/s) | result |
|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|---|---|
| 10000 | 12 | 9988.6 | 599914 | 599926 | 0.0 | 0.0 | 4620 | 0 | 60.9 | 350.9/417.5 | 3231.6 | 135 | 212.9 | localhost:15051: 9988.6 | PASS |

**Knee:** none within the swept range
**Highest passing sustained rate:** 9989 frames/s
