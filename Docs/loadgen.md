# Run LoadGen

LoadGen continuously sends concurrent traffic to the completed Ticketing API. It is a workload
generator, not a data seeder. The process runs without a time limit and stops only when you
press Ctrl+C or terminate its process.

Start the API first. By default, LoadGen expects it at `http://localhost:5107` and verifies
`/openapi/v1.json` before generating traffic.

Run the default read-only comparison profile with a base concurrency of 50:

```powershell
.\scripts\run-loadgen.ps1
```

The launcher targets the `TicketingApi` project created at the repository root by prompt 03.
Use `-ApiDirectory` when running a captured implementation instead:

```powershell
.\scripts\run-loadgen.ps1 -ApiDirectory .\examples\before\TicketingApi
.\scripts\run-loadgen.ps1 -ApiDirectory .\examples\after\TicketingApi
```

`-ApiDirectory` accepts an absolute path or a path relative to the repository root. The
directory must contain `TicketingApi.csproj`. Reports identify the selected implementation as
`run=root`, `run=before`, or `run=after`; use `-RunLabel` to override the inferred name.

The comparison profile repeatedly exercises the query shapes used for before/after analysis:

- Event point read (`event-00001`) as the low-cost control
- Upcoming events
- Events in Memphis
- Orders for `customer-00001`
- Orders for hot event `event-00001`

At startup, LoadGen sends one request for every operation in the selected workload before it
begins weighted traffic. Comparison therefore always hits all five read operations and displays
only those five rows. Mixed always hits and displays all seven read/write operations. A run
fails if any displayed operation has no successful request.

Every report interval, the terminal prints a row per operation with request rate, 2xx/4xx/5xx
and network failures, average and p95 latency, average RU, RU/s, and cumulative RU. Keep the
profile, seed, concurrency, and report interval identical when comparing two implementations.
P95 uses bounded, fine-grained histogram buckets with approximately 5% precision. A high p95 is
not clamped: it means the API response was genuinely slow, which is expected for expensive
unbounded queries in the Before implementation and is the behavior the comparison should expose.
The launcher defaults to a 120-second HTTP request timeout. Override it from 1 to 600 seconds,
for example `-RequestTimeout 300`. In RU columns, `-` means no request completed during the
interval, while `no rsp` means a timeout or network failure completed without an HTTP response,
so no `x-ms-request-charge` header was available. Cumulative RU remains visible when earlier
requests for that operation returned charges. `>=600s` is the terminal latency overflow bucket.

The Before hot-event endpoint returns tens of thousands of orders. At concurrency 50, many of
those large responses run simultaneously and can still exceed a generous timeout. Use the same
concurrency for Before and After comparisons; concurrency 10 is a more stable starting point for
latency measurements, while higher values are useful when deliberately testing saturation.
In an interactive terminal, LoadGen uses a full-screen alternate buffer and refreshes the same
dashboard like `top`; your normal terminal view returns when LoadGen stops. Output redirected
to a file or CI log remains line-oriented. Only one LoadGen instance can run at a time.

Example reproducible comparison run:

```powershell
.\scripts\run-loadgen.ps1 -Workload Comparison -Concurrency 10 -Seed 42 -ReportInterval 2 -Duration 60
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
.\scripts\run-loadgen.ps1 -Workload Mixed -Concurrency 10
```

Use a repeatable traffic pattern or another API URL:

```powershell
.\scripts\run-loadgen.ps1 -Concurrency 25 -Seed 42 -BaseUrl http://localhost:5107
```

To create and reuse a local development token explicitly:

```powershell
$env:TICKETING_API_ACCESS_TOKEN = dotnet user-jwts create --project .\TicketingApi\TicketingApi.csproj --appsettings-file .\appsettings.json --audience http://localhost:5107 --scope Ticketing.Read --scope Ticketing.Write --output token
.\scripts\run-loadgen.ps1
```

To invoke the project directly, set the token in the same PowerShell session first:

```powershell
$env:TICKETING_API_ACCESS_TOKEN = dotnet user-jwts create --project .\TicketingApi\TicketingApi.csproj --appsettings-file .\appsettings.json --audience http://localhost:5107 --scope Ticketing.Read --scope Ticketing.Write --output token
dotnet run --project .\LoadGen\LoadGen.csproj --no-build -- --concurrency 50 --base-url http://localhost:5107
```

For a deployed API, use an HTTPS URL and set `TICKETING_API_ACCESS_TOKEN` to an Entra access
token with `Ticketing.Read`; mixed workloads also require `Ticketing.Write`.

In the mixed profile, LoadGen enters a five-second burst every 30 seconds at ten times the base
concurrency. The comparison profile remains at constant concurrency to reduce measurement
noise. Both profiles discover routes from OpenAPI and print final per-operation totals on exit.

Omit `-Duration` for an unlimited run. Bounded runs stop launching requests at the requested
time, drain requests already in flight, and then print final totals.

## Test LoadGen

Run the isolated LoadGen suite before collecting comparison results:

```powershell
dotnet test .\tests\LoadGen.Tests\LoadGen.Tests.csproj
```

The suite uses a local fake ticketing server and does not access Cosmos DB. It covers argument
validation, root/before/after launcher modes, automatic report labels, both traffic profiles,
guaranteed execution and success gating for every selected operation, all seven operation types,
OpenAPI discovery failures, bearer and idempotency headers, request bodies, status/RU/latency
metrics, p95 precision, single-instance locking, bounded shutdown, and narrow/wide dashboard
rendering.

The fake-server tests validate LoadGen itself. Run each captured API separately with the same
bounded command to validate its live Cosmos DB behavior and collect comparable RU and latency
results.
