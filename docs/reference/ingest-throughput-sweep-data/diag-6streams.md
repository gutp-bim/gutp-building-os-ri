# S22 ingest rate sweep — s221106135528

duration/step 40s · points/gateway 100 · cpu 4 cores (load generator shares the host)

| target/s | streams | achieved/s | accepted | lake rows | loss | dup | peak lag | end lag | drain s | worker CPU% p50/max | worker RSS MB | result |
|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|---|
| 5000 | 6 | 4299.7 | 199210 | 199216 | 0.0 | 0.0 | 0 | 0 | 60.8 | 116.0/142.9 | 1265.3 | FAIL: achieved 86.0% < 95% of target |

**Knee:** first failing step = 5000 frames/s
