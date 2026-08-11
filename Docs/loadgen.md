# Run LoadGen

LoadGen continuously sends concurrent traffic to the completed Ticketing API. It is a workload
generator, not a data seeder. The process runs without a time limit and stops only when you
press Ctrl+C or terminate its process.

Start the API first. By default, LoadGen expects it at `http://localhost:5107` and verifies
`/openapi/v1.json` before generating traffic.

Run with the default base concurrency of 10:

```powershell
.\scripts\run-loadgen.ps1
```

Choose another base concurrency:

```powershell
.\scripts\run-loadgen.ps1 -Concurrency 25
```

Use a repeatable traffic pattern or another API URL:

```powershell
.\scripts\run-loadgen.ps1 -Concurrency 25 -Seed 42 -BaseUrl http://localhost:5107
```

Every 30 seconds, LoadGen enters a five-second burst at ten times the base concurrency. It
discovers the API routes from OpenAPI, reports live request rate and Cosmos DB RU/s, and prints
per-endpoint request-unit totals when it exits.

The launcher intentionally has no duration option. For a bounded diagnostic run, invoke the
LoadGen project directly with its `--duration` argument instead.
