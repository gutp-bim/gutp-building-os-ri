# S26 ingest replica scaling — s261107063902 (r1-base-ing4-48)

commit 08c5c91 · host bench-host · ingress localhost:15051,localhost:15052,localhost:15053,localhost:15054 · rate 20000/s

| streams | replicas | achieved/s | ratio | loss | dup | per replica accepted/s | worker CPU% p50 (each) | NATS CPU% p50/max | loadgen CPU% | peak lag | result |
|--:|--:|--:|--:|--:|--:|---|---|--:|--:|--:|---|
| 48 | 4 | 3527 | 17.6% | 0.0 | 0.0 | 983 / 1027 / 982 / 536 | 40 / 38 / 38 / 34 | 127/144 | 0.5 | 1844 | FAIL: achieved 17.6% < 95% of target |
