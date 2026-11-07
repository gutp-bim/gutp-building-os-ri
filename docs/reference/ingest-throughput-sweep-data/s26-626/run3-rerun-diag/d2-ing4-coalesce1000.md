# S26 ingest replica scaling — s261107064825 (d2-ing4-coalesce1000)

commit 08c5c91 · host bench-host · ingress localhost:15051,localhost:15052,localhost:15053,localhost:15054 · rate 20000/s

| streams | replicas | achieved/s | ratio | loss | dup | per replica accepted/s | worker CPU% p50 (each) | NATS CPU% p50/max | loadgen CPU% | peak lag | result |
|--:|--:|--:|--:|--:|--:|---|---|--:|--:|--:|---|
| 24 | 4 | 7207 | 36.0% | 0.0 | 0.0 | 1687 / 1640 / 1650 / 2230 | 33 / 33 / 32 / 33 | 132/148 | 10.4 | 473827 | FAIL: achieved 36.0% < 95% of target; end lag 465164 > 100000 (=5s of load) |
