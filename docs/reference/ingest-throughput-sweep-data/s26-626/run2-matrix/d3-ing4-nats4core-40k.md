# S26 ingest replica scaling — s261107055802 (d3-ing4-nats4core-40k)

commit 08c5c91 · host Takashi-PC · ingress localhost:15051,localhost:15052,localhost:15053,localhost:15054 · rate 40000/s

| streams | replicas | achieved/s | ratio | loss | dup | per replica accepted/s | worker CPU% p50 (each) | NATS CPU% p50/max | loadgen CPU% | peak lag | result |
|--:|--:|--:|--:|--:|--:|---|---|--:|--:|--:|---|
| 48 | - | - | - | - | - | - | - | - | - | - | FAIL: cell aborted: _InactiveRpcError: <_InactiveRpcError of RPC that terminated with:
	status = StatusCode.DEADLINE_EXCEEDED
	details = "Deadline Exceeded"
	debug_error_string = "DEADLINE_EXCEEDED:Deadline Exceeded"
> |
