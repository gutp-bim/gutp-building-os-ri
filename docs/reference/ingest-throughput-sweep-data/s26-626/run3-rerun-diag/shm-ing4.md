# S26 ingest replica scaling — s261107122120 (shm-ing4)

commit 08c5c91 · host Takashi-PC · ingress localhost:15051,localhost:15052,localhost:15053,localhost:15054 · rate 20000/s

| streams | replicas | achieved/s | ratio | loss | dup | per replica accepted/s | worker CPU% p50 (each) | NATS CPU% p50/max | loadgen CPU% | peak lag | result |
|--:|--:|--:|--:|--:|--:|---|---|--:|--:|--:|---|
| 24 | 4 | 19973 | 99.9% | 0.0 | 0.0 | 4993 / 4993 / 4993 / 4993 | 45 / 45 / 44 / 44 | 61/94 | 35.3 | 8782 | PASS |
