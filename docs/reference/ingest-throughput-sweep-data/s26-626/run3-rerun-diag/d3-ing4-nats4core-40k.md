# S26 ingest replica scaling — s261107070130 (d3-ing4-nats4core-40k)

commit 08c5c91 · host Takashi-PC · ingress localhost:15051,localhost:15052,localhost:15053,localhost:15054 · rate 40000/s

| streams | replicas | achieved/s | ratio | loss | dup | per replica accepted/s | worker CPU% p50 (each) | NATS CPU% p50/max | loadgen CPU% | peak lag | result |
|--:|--:|--:|--:|--:|--:|---|---|--:|--:|--:|---|
| 48 | 4 | 8337 | 20.8% | 0.0 | 0.0 | 2020 / 2086 / 2126 / 2105 | 42 / 41 / 42 / 42 | 206/253 | 1.0 | 1338395 | FAIL: achieved 20.8% < 95% of target; end lag 1338395 > 200000 (=5s of load) |
