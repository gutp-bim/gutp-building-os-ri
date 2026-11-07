# S26 ingest replica scaling — s261107065155 (d3-ing1-nats4core)

commit 08c5c91 · host Takashi-PC · ingress localhost:15051 · rate 20000/s

| streams | replicas | achieved/s | ratio | loss | dup | per replica accepted/s | worker CPU% p50 (each) | NATS CPU% p50/max | loadgen CPU% | peak lag | result |
|--:|--:|--:|--:|--:|--:|---|---|--:|--:|--:|---|
| 24 | 1 | 19966 | 99.8% | 0.0 | 0.0 | 19966 | 162 | 162/190 | 47.6 | 53513 | PASS |
