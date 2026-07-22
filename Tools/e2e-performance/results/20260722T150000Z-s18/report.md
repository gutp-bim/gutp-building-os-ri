# 100 Gateway reconnect evaluation (#262)

Run: `20260722T150000Z-s18`<br>
Result: **PASS**<br>
Load: **100 Gateway**, reconnect concentration **500 ms**

| KPI | measured |
|:--|--:|
| reconnected | 100/100 |
| convergence | 2693.181 ms |
| ingress accepted/rejected | 100/100 |
| lake rows / loss / duplicates | 100 / 0 / 0 |
| control accepted/succeeded | 100/100 |

| service | error log count |
|:--|--:|
| gateway-bridge | 0 |
| connector-worker | 0 |
| api-server | 0 |

Exceeded: none
