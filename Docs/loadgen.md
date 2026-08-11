# Run LoadGen

LoadGen continuously sends concurrent traffic to the completed Ticketing API. It is a workload
generator, not a data seeder. The process runs without a time limit and stops only when you
press Ctrl+C or terminate its process.

Start the API first. By default, LoadGen expects it at `http://localhost:5107` and verifies
`/openapi/v1.json` before generating traffic.

Run the default read-only comparison profile with a base concurrency of 10:

```powershell
.\scripts\run-loadgen.ps1
```

The comparison profile repeatedly exercises the query shapes used for before/after analysis:

- Event point read (`event-00001`) as the low-cost control
- Upcoming events
- Events in Memphis
- Orders for `customer-00001`
- Orders for hot event `event-00001`

Every report interval, the terminal prints a row per operation with request rate, 2xx/4xx/5xx
and network failures, average and p95 latency, average RU, RU/s, and cumulative RU. Keep the
profile, seed, concurrency, and report interval identical when comparing two implementations.
In an interactive terminal, LoadGen uses a full-screen alternate buffer and refreshes the same
dashboard like `top`; your normal terminal view returns when LoadGen stops. Output redirected
to a file or CI log remains line-oriented. Only one LoadGen instance can run at a time.

Example reproducible comparison run:

```powershell
.\scripts\run-loadgen.ps1 -Profile Comparison -Concurrency 10 -Seed 42 -ReportInterval 2
```

For localhost, the launcher automatically creates a development token when none is supplied.
The comparison profile requests `Ticketing.Read`; the mixed profile also requests
`Ticketing.Write`. If this is the first token created for the project, rebuild and restart the
API once so it loads the generated signing configuration.

Choose another base concurrency:

```powershell
.\scripts\run-loadgen.ps1 -Concurrency 25
```

Run the original mixed read/write workload, including purchase bursts and event creation:

```powershell
.\scripts\run-loadgen.ps1 -Profile Mixed -Concurrency 10
```

Use a repeatable traffic pattern or another API URL:

```powershell
.\scripts\run-loadgen.ps1 -Concurrency 25 -Seed 42 -BaseUrl http://localhost:5107
```

To create and reuse a local development token explicitly:

```powershell
$env:TICKETING_API_ACCESS_TOKEN = dotnet user-jwts create --project .\TicketingApi\TicketingApi.csproj --appsettings-file ..\appsettings.json --scope Ticketing.Read --scope Ticketing.Write --output token
.\scripts\run-loadgen.ps1
```

To invoke the project directly, set the token in the same PowerShell session first:

```powershell
$env:TICKETING_API_ACCESS_TOKEN = dotnet user-jwts create --project .\TicketingApi\TicketingApi.csproj --appsettings-file ..\appsettings.json --scope Ticketing.Read --scope Ticketing.Write --output token
dotnet run --project .\LoadGen\LoadGen.csproj --no-build -- --concurrency 50 --base-url http://localhost:5107
```

For a deployed API, use an HTTPS URL and set `TICKETING_API_ACCESS_TOKEN` to an Entra access
token with `Ticketing.Read`; mixed workloads also require `Ticketing.Write`.

In the mixed profile, LoadGen enters a five-second burst every 30 seconds at ten times the base
concurrency. The comparison profile remains at constant concurrency to reduce measurement
noise. Both profiles discover routes from OpenAPI and print final per-operation totals on exit.

The launcher intentionally has no duration option. For a bounded diagnostic run, invoke the
LoadGen project directly with its `--duration` argument instead.
