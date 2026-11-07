# S26 ingest replica scaling — s261107065420 (d3-ing4-nats4core)

commit 08c5c91 · host bench-host · ingress localhost:15051,localhost:15052,localhost:15053,localhost:15054 · rate 20000/s

| streams | replicas | achieved/s | ratio | loss | dup | per replica accepted/s | worker CPU% p50 (each) | NATS CPU% p50/max | loadgen CPU% | peak lag | result |
|--:|--:|--:|--:|--:|--:|---|---|--:|--:|--:|---|
| 24 | 4 | 7914 | 39.6% | 0.0 | 0.0 | 1930 / 1918 / 1872 / 2195 | 42 / 43 / 42 / 44 | 197/238 | 8.4 | 539253 | FAIL: achieved 39.6% < 95% of target; end lag 526121 > 100000 (=5s of load) |
