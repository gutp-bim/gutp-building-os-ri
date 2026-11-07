# S26 ingest replica scaling — s261107011147 (r1-after-ing2)

commit 08c5c91 · host Takashi-PC · ingress localhost:15051,localhost:15052 · rate 20000/s

| streams | replicas | achieved/s | ratio | loss | dup | per replica accepted/s | worker CPU% p50 (each) | NATS CPU% p50/max | loadgen CPU% | peak lag | result |
|--:|--:|--:|--:|--:|--:|---|---|--:|--:|--:|---|
| 12 | 2 | 19958 | 99.8% | 0.0 | 0.0 | 9979 / 9979 | 95 / 97 | 138/148 | 49.2 | 396600 | FAIL: end lag 396600 > 100000 (=5s of load) |
| 24 | 2 | 19964 | 99.8% | 0.0 | 0.0 | 9982 / 9982 | 109 / 109 | 145/155 | 58.2 | 89665 | PASS |
| 48 | 2 | 19961 | 99.8% | 0.0 | 0.0 | 9980 / 9980 | 124 / 124 | 151/158 | 72.7 | 59440 | PASS |
