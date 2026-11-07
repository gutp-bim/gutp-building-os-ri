# S26 ingest replica scaling — s261107044656 (r2-B-all)

commit 08c5c91 · host bench-host · ingress localhost:15051 · rate 10000/s

| streams | replicas | achieved/s | ratio | loss | dup | per replica accepted/s | worker CPU% p50 (each) | NATS CPU% p50/max | loadgen CPU% | peak lag | result |
|--:|--:|--:|--:|--:|--:|---|---|--:|--:|--:|---|
| 12 | 1 | 5867 | 58.7% | 0.0 | 0.0 | 5867 | 130 | 63/74 | 3.5 | 1499 | FAIL: achieved 58.7% < 95% of target |
