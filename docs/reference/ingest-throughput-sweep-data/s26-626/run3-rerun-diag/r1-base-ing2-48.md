# S26 ingest replica scaling — s261107063454 (r1-base-ing2-48)

commit 08c5c91 · host Takashi-PC · ingress localhost:15051,localhost:15052 · rate 20000/s

| streams | replicas | achieved/s | ratio | loss | dup | per replica accepted/s | worker CPU% p50 (each) | NATS CPU% p50/max | loadgen CPU% | peak lag | result |
|--:|--:|--:|--:|--:|--:|---|---|--:|--:|--:|---|
| 48 | 2 | 7777 | 38.9% | 0.0 | 0.0 | 3922 / 3855 | 76 / 77 | 131/141 | 1.0 | 4600 | FAIL: achieved 38.9% < 95% of target |
