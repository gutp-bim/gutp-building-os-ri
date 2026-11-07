# S26 ingest replica scaling — s261107123433 (shmhi-ing4-80k)

commit 08c5c91 · host Takashi-PC · ingress localhost:15051,localhost:15052,localhost:15053,localhost:15054 · rate 80000/s

| streams | replicas | achieved/s | ratio | loss | dup | per replica accepted/s | worker CPU% p50 (each) | NATS CPU% p50/max | loadgen CPU% | peak lag | result |
|--:|--:|--:|--:|--:|--:|---|---|--:|--:|--:|---|
| 48 | 4 | 72387 | 90.5% | 0.0 | 0.0 | 18288 / 17977 / 18090 / 18032 | 150 / 150 / 153 / 148 | 166/179 | 158.2 | 3792879 | FAIL: achieved 90.5% < 95% of target; end lag 3792879 > 400000 (=5s of load) |
