# S26 ingest replica scaling — s261107121637 (shm-ing1)

commit 08c5c91 · host Takashi-PC · ingress localhost:15051 · rate 20000/s

| streams | replicas | achieved/s | ratio | loss | dup | per replica accepted/s | worker CPU% p50 (each) | NATS CPU% p50/max | loadgen CPU% | peak lag | result |
|--:|--:|--:|--:|--:|--:|---|---|--:|--:|--:|---|
| 24 | 1 | 19969 | 99.9% | 0.0 | 0.0 | 19969 | 79 | 49/63 | 30.9 | 788 | PASS |
