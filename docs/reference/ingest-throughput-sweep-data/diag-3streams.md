# S22 ingest rate sweep — s221106135326

duration/step 40s · points/gateway 100 · cpu 4 cores (load generator shares the host)

| target/s | streams | achieved/s | accepted | lake rows | loss | dup | peak lag | end lag | drain s | worker CPU% p50/max | worker RSS MB | result |
|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|---|
| 5000 | 3 | 3291.6 | 182916 | 182919 | 0.0 | 0.0 | 0 | 0 | 60.9 | 126.4/147.0 | 1262.3 | FAIL: achieved 65.8% < 95% of target |

**Knee:** first failing step = 5000 frames/s
