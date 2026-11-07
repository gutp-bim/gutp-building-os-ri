# S26 ingest replica scaling — s261107123047 (shmhi-ing2-60k)

commit 08c5c91 · host Takashi-PC · ingress localhost:15051,localhost:15052 · rate 60000/s

| streams | replicas | achieved/s | ratio | loss | dup | per replica accepted/s | worker CPU% p50 (each) | NATS CPU% p50/max | loadgen CPU% | peak lag | result |
|--:|--:|--:|--:|--:|--:|---|---|--:|--:|--:|---|
| 48 | 2 | 50585 | 84.3% | 0.0 | 0.0 | 25349 / 25236 | 195 / 196 | 147/164 | 108.6 | 1978948 | FAIL: achieved 84.3% < 95% of target; end lag 1977919 > 300000 (=5s of load) |
