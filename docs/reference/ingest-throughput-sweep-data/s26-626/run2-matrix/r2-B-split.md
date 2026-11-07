# S26 ingest replica scaling — s261107045249 (r2-B-split)

commit 08c5c91 · host bench-host · ingress localhost:15051 · rate 10000/s

| streams | replicas | achieved/s | ratio | loss | dup | per replica accepted/s | worker CPU% p50 (each) | NATS CPU% p50/max | loadgen CPU% | peak lag | result |
|--:|--:|--:|--:|--:|--:|---|---|--:|--:|--:|---|
| 12 | 1 | 4368 | 43.7% | 0.0 | 0.0 | 4368 | 82 | 79/95 | 1.5 | 1142 | FAIL: achieved 43.7% < 95% of target |
