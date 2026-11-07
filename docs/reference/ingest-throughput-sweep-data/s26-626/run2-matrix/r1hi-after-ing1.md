# S26 ingest replica scaling — s261107020005 (r1hi-after-ing1)

commit 08c5c91 · host bench-host · ingress localhost:15051 · rate 40000/s

| streams | replicas | achieved/s | ratio | loss | dup | per replica accepted/s | worker CPU% p50 (each) | NATS CPU% p50/max | loadgen CPU% | peak lag | result |
|--:|--:|--:|--:|--:|--:|---|---|--:|--:|--:|---|
| 48 | 1 | 30948 | 77.4% | 0.0 | 0.0 | 30948 | 195 | 123/135 | 94.7 | 460363 | FAIL: achieved 77.4% < 95% of target; end lag 460363 > 200000 (=5s of load) |
