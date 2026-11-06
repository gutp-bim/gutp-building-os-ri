# S22 ingest rate sweep — s221106134137

duration/step 60s · points/gateway 100 · cpu 4 cores (load generator shares the host)

| target/s | streams | achieved/s | accepted | lake rows | loss | dup | peak lag | end lag | drain s | worker CPU% p50/max | worker RSS MB | result |
|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|---|
| 500 | 1 | 499.8 | 29994 | 29995 | 0.0 | 0.0 | 0 | 0 | 40.7 | 56.8/95.1 | 646.6 | PASS |
| 1000 | 1 | 999.8 | 59997 | 59998 | 0.0 | 0.0 | 0 | 0 | 60.6 | 88.7/121.2 | 970.6 | PASS |
| 2000 | 1 | 1582.7 | 118599 | 118600 | 0.0 | 0.0 | 0 | 0 | 60.6 | 114.1/126.2 | 1029.9 | FAIL: achieved 79.1% < 95% of target |

**Knee:** first failing step = 2000 frames/s
**Highest passing sustained rate:** 1000 frames/s
