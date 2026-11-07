# S26 ingest replica scaling — s261107013136 (r1-after-ing4)

commit 08c5c91 · host bench-host · ingress localhost:15051,localhost:15052,localhost:15053,localhost:15054 · rate 20000/s

| streams | replicas | achieved/s | ratio | loss | dup | per replica accepted/s | worker CPU% p50 (each) | NATS CPU% p50/max | loadgen CPU% | peak lag | result |
|--:|--:|--:|--:|--:|--:|---|---|--:|--:|--:|---|
| 12 | 4 | 8707 | 43.5% | 0.0 | 0.0 | 2169 / 2236 / 2142 / 2160 | 41 / 40 / 41 / 41 | 152/163 | 15.7 | 252722 | FAIL: achieved 43.5% < 95% of target; end lag 252722 > 100000 (=5s of load) |
| 24 | 4 | 8696 | 43.5% | 0.0 | 0.0 | 1945 / 2197 / 2280 / 2274 | 41 / 41 / 43 / 42 | 159/178 | 2.5 | 634627 | FAIL: achieved 43.5% < 95% of target; end lag 634627 > 100000 (=5s of load) |
| 48 | 4 | 9462 | 47.3% | 0.0 | 0.0 | 2249 / 2386 / 2245 / 2582 | 47 / 47 / 46 / 50 | 166/179 | 4.4 | 683973 | FAIL: achieved 47.3% < 95% of target; end lag 683973 > 100000 (=5s of load) |
