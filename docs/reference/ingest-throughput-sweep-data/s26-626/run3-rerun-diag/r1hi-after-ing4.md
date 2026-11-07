# S26 ingest replica scaling — s261107062931 (r1hi-after-ing4)

commit 08c5c91 · host Takashi-PC · ingress localhost:15051,localhost:15052,localhost:15053,localhost:15054 · rate 40000/s

| streams | replicas | achieved/s | ratio | loss | dup | per replica accepted/s | worker CPU% p50 (each) | NATS CPU% p50/max | loadgen CPU% | peak lag | result |
|--:|--:|--:|--:|--:|--:|---|---|--:|--:|--:|---|
| 48 | 4 | 8281 | 20.7% | 0.0 | 0.0 | 2192 / 2039 / 1994 / 2056 | 43 / 40 / 41 / 40 | 161/185 | 1.5 | 1144232 | FAIL: achieved 20.7% < 95% of target; end lag 1144232 > 200000 (=5s of load) |
