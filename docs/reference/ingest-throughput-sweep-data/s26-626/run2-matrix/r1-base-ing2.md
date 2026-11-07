# S26 ingest replica scaling — s261107030507 (r1-base-ing2)

commit 08c5c91 · host Takashi-PC · ingress localhost:15051,localhost:15052 · rate 20000/s

| streams | replicas | achieved/s | ratio | loss | dup | per replica accepted/s | worker CPU% p50 (each) | NATS CPU% p50/max | loadgen CPU% | peak lag | result |
|--:|--:|--:|--:|--:|--:|---|---|--:|--:|--:|---|
| 12 | 2 | 4452 | 22.3% | 0.0 | 0.0 | 2234 / 2218 | 72 / 73 | 109/117 | 1.5 | 2769 | FAIL: achieved 22.3% < 95% of target |
| 24 | 2 | 6465 | 32.3% | 0.0 | 0.0 | 3240 / 3226 | 74 / 73 | 121/130 | 0.5 | 2813 | FAIL: achieved 32.3% < 95% of target |
| 48 | - | - | - | - | - | - | - | - | - | - | FAIL: cell aborted: _InactiveRpcError: <_InactiveRpcError of RPC that terminated with:
	status = StatusCode.DEADLINE_EXCEEDED
	details = "Deadline Exceeded"
	debug_error_string = "DEADLINE_EXCEEDED:Deadline Exceeded"
> |
