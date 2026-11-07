# S26 ingest replica scaling — s261107071445 (r4-ing2-14k-1h)

commit 08c5c91 · host bench-host · ingress localhost:15051,localhost:15052 · rate 14000/s

| streams | replicas | achieved/s | ratio | loss | dup | per replica accepted/s | worker CPU% p50 (each) | NATS CPU% p50/max | loadgen CPU% | peak lag | result |
|--:|--:|--:|--:|--:|--:|---|---|--:|--:|--:|---|
| 24 | 2 | 13992 | 100.0% | 0.0 | 0.0 | 6996 / 6996 | 80 / 80 | 128/155 | 35.6 | 17847 | PASS |
