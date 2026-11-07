# S26 ingest replica scaling — s261107081906 (e1-ing4-load1)

commit 08c5c91 · host Takashi-PC · ingress localhost:15051 · rate 20000/s

| streams | replicas | achieved/s | ratio | loss | dup | per replica accepted/s | worker CPU% p50 (each) | NATS CPU% p50/max | loadgen CPU% | peak lag | result |
|--:|--:|--:|--:|--:|--:|---|---|--:|--:|--:|---|
| 24 | 1 | 7774 | 38.9% | 0.0 | 0.0 | 7774 | 65 | 156/169 | 1.0 | 536204 | FAIL: achieved 38.9% < 95% of target; end lag 536204 > 100000 (=5s of load) |
