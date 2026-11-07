# S26 ingest replica scaling — s261107040452 (r1-afterlegacy-ing1)

commit 08c5c91 · host Takashi-PC · ingress localhost:15051 · rate 20000/s

| streams | replicas | achieved/s | ratio | loss | dup | per replica accepted/s | worker CPU% p50 (each) | NATS CPU% p50/max | loadgen CPU% | peak lag | result |
|--:|--:|--:|--:|--:|--:|---|---|--:|--:|--:|---|
| 24 | 1 | 7235 | 36.2% | 0.0 | 0.0 | 7235 | 91 | 97/111 | 1.0 | 5127 | FAIL: achieved 36.2% < 95% of target |
