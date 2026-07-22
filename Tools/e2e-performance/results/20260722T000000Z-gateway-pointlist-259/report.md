# Gateway Point List 10,000-point evaluation (#259)

The test generates ten buildings and 10,000 points in a real OxiGraph container. Each building owns
1,000 points through a distinct gateway. It requests `GW-SCALE-00` through
`GatewayProvisioningController`, serializes the full response contract, and verifies that only the
expected 1,000 point IDs are returned.

| Metric | Before | After | Budget |
|---|---:|---:|---:|
| OxiGraph query time | 26,632.6013 ms | 91.4226 ms | — |
| API response construction + JSON serialization | not measured | 259.8261 ms | 5,000 ms |
| Returned points | — | 1,000 | 1,000 |

The legacy OxiGraph query alone exceeded the API budget, so a separate legacy API time would not add
useful pass/fail information. The optimized query is 291.32 times faster in this run. Its first query
resolves the gateway-owned point URIs; two parallel, `VALUES`-constrained queries then retrieve direct
attributes and owning equipment without expanding optional joins across the whole Twin.

Reproduce from `DotNet/`:

```bash
dotnet test BuildingOS.IntegrationTest/BuildingOS.IntegrationTest.csproj \
  --filter FullyQualifiedName~GatewayPointListScaleTest
```

Raw machine-readable measurements and the exact OxiGraph image digest are in `kpi-summary.json`.
