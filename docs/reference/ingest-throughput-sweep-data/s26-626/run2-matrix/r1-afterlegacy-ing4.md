# S26 ingest replica scaling — s261107042347 (r1-afterlegacy-ing4)

commit 08c5c91 · host bench-host · ingress localhost:15051,localhost:15052,localhost:15053,localhost:15054 · rate 20000/s

| streams | replicas | achieved/s | ratio | loss | dup | per replica accepted/s | worker CPU% p50 (each) | NATS CPU% p50/max | loadgen CPU% | peak lag | result |
|--:|--:|--:|--:|--:|--:|---|---|--:|--:|--:|---|
| 24 | 4 | 3259 | 16.3% | 0.0 | 0.0 | 829 / 822 / 800 / 808 | 46 / 45 / 46 / 45 | 127/142 | 1.0 | 1485 | FAIL: achieved 16.3% < 95% of target |
