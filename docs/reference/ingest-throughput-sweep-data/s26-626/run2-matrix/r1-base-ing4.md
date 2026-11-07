# S26 ingest replica scaling — s261107033330 (r1-base-ing4)

commit 08c5c91 · host bench-host · ingress localhost:15051,localhost:15052,localhost:15053,localhost:15054 · rate 20000/s

| streams | replicas | achieved/s | ratio | loss | dup | per replica accepted/s | worker CPU% p50 (each) | NATS CPU% p50/max | loadgen CPU% | peak lag | result |
|--:|--:|--:|--:|--:|--:|---|---|--:|--:|--:|---|
| 12 | 4 | 2878 | 14.4% | 0.0 | 0.0 | 718 / 737 / 740 / 682 | 50 / 52 / 50 / 52 | 132/145 | 1.0 | 954 | FAIL: achieved 14.4% < 95% of target |
| 24 | 4 | 3273 | 16.4% | 0.0 | 0.0 | 841 / 833 / 832 / 768 | 40 / 41 / 41 / 42 | 129/143 | 0.5 | 1884 | FAIL: achieved 16.4% < 95% of target |
| 48 | - | - | - | - | - | - | - | - | - | - | FAIL: cell aborted: _InactiveRpcError: <_InactiveRpcError of RPC that terminated with:
	status = StatusCode.DEADLINE_EXCEEDED
	details = "Deadline Exceeded"
	debug_error_string = "DEADLINE_EXCEEDED:Deadline Exceeded"
> |
