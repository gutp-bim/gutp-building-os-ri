# S26 ingest replica scaling — s261107050606 (r3-cluster-r3)

commit 08c5c91 · host Takashi-PC · ingress localhost:15051,localhost:15052 · rate 20000/s

| streams | replicas | achieved/s | ratio | loss | dup | per replica accepted/s | worker CPU% p50 (each) | NATS CPU% p50/max | loadgen CPU% | peak lag | result |
|--:|--:|--:|--:|--:|--:|---|---|--:|--:|--:|---|
| 24 | - | - | - | - | - | - | - | - | - | - | FAIL: cell aborted: _InactiveRpcError: <_InactiveRpcError of RPC that terminated with:
	status = StatusCode.DEADLINE_EXCEEDED
	details = "Deadline Exceeded"
	debug_error_string = "DEADLINE_EXCEEDED:Deadline Exceeded"
> |
