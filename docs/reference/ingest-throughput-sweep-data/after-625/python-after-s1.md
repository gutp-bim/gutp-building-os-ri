# S22 ingest rate sweep — s221106220225

duration/step 60s · points/gateway 100 · cpu 16 cores (load generator shares the host)

| target/s | streams | achieved/s | accepted | lake rows | loss | dup | peak lag | end lag | drain s | worker CPU% p50/max | worker RSS MB | result |
|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|---|
| 2000 | 1 | 1998.2 | 119992 | 119993 | 0.0 | 0.0 | 367 | 0 | 61.0 | 177.6/301.3 | 1688.5 | PASS |
| 5000 | 1 | 2888.0 | 207901 | 207902 | 0.0 | 0.0 | 1182 | 0 | 60.7 | 232.6/303.4 | 2040.1 | FAIL: achieved 57.8% < 95% of target |
| 8000 | 1 | 2955.7 | 273140 | 273141 | 0.0 | 0.0 | 1307 | 0 | 60.6 | 228.7/297.8 | 2557.8 | FAIL: achieved 36.9% < 95% of target |

**Knee:** first failing step = 5000 frames/s
**Highest passing sustained rate:** 1998 frames/s
