# S26 ingest replica scaling — s261107082252 (e2-ing4-loadall)

commit 08c5c91 · host Takashi-PC · ingress localhost:15051,localhost:15052,localhost:15053,localhost:15054 · rate 20000/s

| streams | replicas | achieved/s | ratio | loss | dup | per replica accepted/s | worker CPU% p50 (each) | NATS CPU% p50/max | loadgen CPU% | peak lag | result |
|--:|--:|--:|--:|--:|--:|---|---|--:|--:|--:|---|
| 24 | 4 | 9676 | 48.4% | 0.0 | 0.0 | 2450 / 2402 / 2365 / 2458 | 40 / 39 / 40 / 39 | 159/171 | 25.2 | 448523 | FAIL: achieved 48.4% < 95% of target; end lag 448523 > 100000 (=5s of load) |
