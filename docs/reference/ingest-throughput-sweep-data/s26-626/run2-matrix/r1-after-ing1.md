# S26 ingest replica scaling — s261107005500 (r1-after-ing1)

commit 08c5c91 · host bench-host · ingress localhost:15051 · rate 20000/s

| streams | replicas | achieved/s | ratio | loss | dup | per replica accepted/s | worker CPU% p50 (each) | NATS CPU% p50/max | loadgen CPU% | peak lag | result |
|--:|--:|--:|--:|--:|--:|---|---|--:|--:|--:|---|
| 12 | 1 | 19962 | 99.8% | 0.0 | 0.0 | 19962 | 130 | 107/128 | 49.1 | 15092 | PASS |
| 24 | 1 | 19973 | 99.9% | 0.0 | 0.0 | 19973 | 142 | 109/122 | 50.7 | 10767 | PASS |
| 48 | 1 | 19957 | 99.8% | 0.0 | 0.0 | 19957 | 153 | 112/124 | 65.8 | 9446 | PASS |
