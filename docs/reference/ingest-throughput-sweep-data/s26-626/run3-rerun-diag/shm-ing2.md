# S26 ingest replica scaling — s261107121859 (shm-ing2)

commit 08c5c91 · host bench-host · ingress localhost:15051,localhost:15052 · rate 20000/s

| streams | replicas | achieved/s | ratio | loss | dup | per replica accepted/s | worker CPU% p50 (each) | NATS CPU% p50/max | loadgen CPU% | peak lag | result |
|--:|--:|--:|--:|--:|--:|---|---|--:|--:|--:|---|
| 24 | 2 | 19971 | 99.9% | 0.0 | 0.0 | 9986 / 9986 | 56 / 56 | 55/68 | 32.8 | 8142 | PASS |
