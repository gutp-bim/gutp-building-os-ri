# S26 ingest replica scaling — s261107082935 (prof-one)

commit 08c5c91 · host bench-host · ingress localhost:15051 · rate 20000/s

| streams | replicas | achieved/s | ratio | loss | dup | per replica accepted/s | worker CPU% p50 (each) | NATS CPU% p50/max | loadgen CPU% | peak lag | result |
|--:|--:|--:|--:|--:|--:|---|---|--:|--:|--:|---|
| 24 | 1 | 19964 | 99.8% | 0.0 | 0.0 | 19964 | 121 | 99/115 | 42.2 | 25743 | PASS |
