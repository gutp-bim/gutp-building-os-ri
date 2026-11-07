# S26 ingest replica scaling — s261107082545 (e3-ing1)

commit 08c5c91 · host Takashi-PC · ingress localhost:15051 · rate 20000/s

| streams | replicas | achieved/s | ratio | loss | dup | per replica accepted/s | worker CPU% p50 (each) | NATS CPU% p50/max | loadgen CPU% | peak lag | result |
|--:|--:|--:|--:|--:|--:|---|---|--:|--:|--:|---|
| 24 | 1 | 19972 | 99.9% | 0.0 | 0.0 | 19972 | 112 | 96/110 | 40.2 | 8135 | PASS |
