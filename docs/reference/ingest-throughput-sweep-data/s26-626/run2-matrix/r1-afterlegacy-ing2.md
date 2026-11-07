# S26 ingest replica scaling — s261107041410 (r1-afterlegacy-ing2)

commit 08c5c91 · host bench-host · ingress localhost:15051,localhost:15052 · rate 20000/s

| streams | replicas | achieved/s | ratio | loss | dup | per replica accepted/s | worker CPU% p50 (each) | NATS CPU% p50/max | loadgen CPU% | peak lag | result |
|--:|--:|--:|--:|--:|--:|---|---|--:|--:|--:|---|
| 24 | 2 | 6067 | 30.3% | 0.0 | 0.0 | 3040 / 3027 | 76 / 76 | 119/132 | 1.0 | 1074 | FAIL: achieved 30.3% < 95% of target |
