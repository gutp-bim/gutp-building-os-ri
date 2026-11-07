# S26 ingest replica scaling — s261107122342 (shm-ing4-40k)

commit 08c5c91 · host Takashi-PC · ingress localhost:15051,localhost:15052,localhost:15053,localhost:15054 · rate 40000/s

| streams | replicas | achieved/s | ratio | loss | dup | per replica accepted/s | worker CPU% p50 (each) | NATS CPU% p50/max | loadgen CPU% | peak lag | result |
|--:|--:|--:|--:|--:|--:|---|---|--:|--:|--:|---|
| 48 | 4 | 39915 | 99.8% | 0.0 | 0.0 | 9979 / 9979 / 9979 / 9979 | 107 / 107 / 106 / 108 | 128/151 | 118.4 | 378328 | FAIL: end lag 378328 > 200000 (=5s of load) |
