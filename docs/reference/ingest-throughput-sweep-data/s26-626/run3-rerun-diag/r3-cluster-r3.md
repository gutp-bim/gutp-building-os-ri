# S26 ingest replica scaling — s261107071221 (r3-cluster-r3)

commit 08c5c91 · host bench-host · ingress localhost:15051,localhost:15052 · rate 20000/s

| streams | replicas | achieved/s | ratio | loss | dup | per replica accepted/s | worker CPU% p50 (each) | NATS CPU% p50/max | loadgen CPU% | peak lag | result |
|--:|--:|--:|--:|--:|--:|---|---|--:|--:|--:|---|
| 24 | 2 | 19973 | 99.9% | 0.0 | 0.0 | 9986 / 9986 | 90 / 89 | 79/87 | 55.0 | 0 | PASS |
