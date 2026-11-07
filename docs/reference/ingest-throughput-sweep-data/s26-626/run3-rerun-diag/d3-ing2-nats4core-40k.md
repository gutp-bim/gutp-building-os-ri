# S26 ingest replica scaling — s261107065752 (d3-ing2-nats4core-40k)

commit 08c5c91 · host bench-host · ingress localhost:15051,localhost:15052 · rate 40000/s

| streams | replicas | achieved/s | ratio | loss | dup | per replica accepted/s | worker CPU% p50 (each) | NATS CPU% p50/max | loadgen CPU% | peak lag | result |
|--:|--:|--:|--:|--:|--:|---|---|--:|--:|--:|---|
| 48 | 2 | 22911 | 57.3% | 0.0 | 0.0 | 12224 / 10687 | 132 / 131 | 214/232 | 42.2 | 1540562 | FAIL: achieved 57.3% < 95% of target; end lag 1540562 > 200000 (=5s of load) |
