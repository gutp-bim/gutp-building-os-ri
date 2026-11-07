# S26 ingest replica scaling — s261107021236 (r1hi-after-ing2)

commit 08c5c91 · host bench-host · ingress localhost:15051,localhost:15052 · rate 40000/s

| streams | replicas | achieved/s | ratio | loss | dup | per replica accepted/s | worker CPU% p50 (each) | NATS CPU% p50/max | loadgen CPU% | peak lag | result |
|--:|--:|--:|--:|--:|--:|---|---|--:|--:|--:|---|
| 48 | 2 | 28879 | 72.2% | 0.0 | 0.0 | 15152 / 13727 | 138 / 139 | 155/168 | 64.3 | 1548989 | FAIL: achieved 72.2% < 95% of target; end lag 1548989 > 200000 (=5s of load) |
