# S26 ingest replica scaling — s261107122759 (shmhi-ing1-40k)

commit 08c5c91 · host bench-host · ingress localhost:15051 · rate 40000/s

| streams | replicas | achieved/s | ratio | loss | dup | per replica accepted/s | worker CPU% p50 (each) | NATS CPU% p50/max | loadgen CPU% | peak lag | result |
|--:|--:|--:|--:|--:|--:|---|---|--:|--:|--:|---|
| 48 | 1 | 35754 | 89.4% | 0.0 | 0.0 | 35754 | 197 | 108/123 | 90.9 | 29671 | FAIL: achieved 89.4% < 95% of target |
