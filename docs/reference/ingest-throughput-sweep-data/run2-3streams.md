# S22 ingest rate sweep — s221106134809

duration/step 60s · points/gateway 100 · cpu 4 cores (load generator shares the host)

| target/s | streams | achieved/s | accepted | lake rows | loss | dup | peak lag | end lag | drain s | worker CPU% p50/max | worker RSS MB | result |
|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|---|
| 2000 | 3 | 1999.2 | 119983 | 119986 | 0.0 | 0.0 | 219 | 0 | 60.8 | 108.1/129.7 | 1122.2 | PASS |
| 5000 | 3 | 3241.1 | 292897 | 292900 | 0.0 | 0.0 | 1 | 0 | 60.7 | 127.6/146.2 | 1254.2 | FAIL: achieved 64.8% < 95% of target |

**Knee:** first failing step = 5000 frames/s
**Highest passing sustained rate:** 1999 frames/s
