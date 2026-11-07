# S26 ingest replica scaling — s261107070603 (r3-sync-always)

commit 08c5c91 · host bench-host · ingress localhost:15051,localhost:15052 · rate 20000/s

| streams | replicas | achieved/s | ratio | loss | dup | per replica accepted/s | worker CPU% p50 (each) | NATS CPU% p50/max | loadgen CPU% | peak lag | result |
|--:|--:|--:|--:|--:|--:|---|---|--:|--:|--:|---|
| 24 | 2 | 3 | 0.0% | 0.0 | 0.0 | 1 / 1 | 7 / 7 | 13/18 | 0.5 | 150136 | FAIL: achieved 0.0% < 95% of target; end lag 150136 > 100000 (=5s of load) |
