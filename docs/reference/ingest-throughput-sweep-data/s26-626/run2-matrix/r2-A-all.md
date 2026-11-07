# S26 ingest replica scaling — s261107043622 (r2-A-all)

commit 08c5c91 · host bench-host · ingress localhost:15051 · rate 10000/s

| streams | replicas | achieved/s | ratio | loss | dup | per replica accepted/s | worker CPU% p50 (each) | NATS CPU% p50/max | loadgen CPU% | peak lag | result |
|--:|--:|--:|--:|--:|--:|---|---|--:|--:|--:|---|
| 12 | 1 | 9985 | 99.9% | 0.0 | 0.0 | 9985 | 112 | 44/63 | 22.9 | 14 | PASS |
