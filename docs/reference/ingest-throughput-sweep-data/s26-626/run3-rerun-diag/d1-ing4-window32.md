# S26 ingest replica scaling — s261107064531 (d1-ing4-window32)

commit 08c5c91 · host bench-host · ingress localhost:15051,localhost:15052,localhost:15053,localhost:15054 · rate 20000/s

| streams | replicas | achieved/s | ratio | loss | dup | per replica accepted/s | worker CPU% p50 (each) | NATS CPU% p50/max | loadgen CPU% | peak lag | result |
|--:|--:|--:|--:|--:|--:|---|---|--:|--:|--:|---|
| 24 | 4 | 8112 | 40.6% | 0.0 | 0.0 | 2017 / 1998 / 2030 / 2067 | 41 / 41 / 42 / 44 | 173/182 | 4.9 | 28358 | FAIL: achieved 40.6% < 95% of target |
