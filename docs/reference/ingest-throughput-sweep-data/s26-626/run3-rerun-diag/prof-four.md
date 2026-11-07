# S26 ingest replica scaling — s261107121257 (prof-four)

commit 08c5c91 · host bench-host · ingress localhost:15051 · rate 20000/s

| streams | replicas | achieved/s | ratio | loss | dup | per replica accepted/s | worker CPU% p50 (each) | NATS CPU% p50/max | loadgen CPU% | peak lag | result |
|--:|--:|--:|--:|--:|--:|---|---|--:|--:|--:|---|
| 24 | 1 | 7105 | 35.5% | 0.0 | 0.0 | 7105 | 63 | 153/167 | 0.5 | 415118 | FAIL: achieved 35.5% < 95% of target; end lag 415118 > 100000 (=5s of load) |
