# Run LoadGen

LoadGen continuously sends concurrent traffic to the completed Ticketing API. It is a workload
generator, not a data seeder. The process runs without a time limit and stops only when you
press Ctrl+C or terminate its process.

Start the API first. By default, LoadGen expects it at `http://localhost:5107` and verifies
`/openapi/v1.json` before generating traffic.

Running the launcher without arguments in an interactive terminal opens a guided setup for the
API target, workload, concurrency, duration, request timeout, URL, refresh interval, and optional
random seed:

```powershell
.\scripts\run-loadgen.ps1
```

The API directory has no default and must be entered. Press Enter to accept defaults for the
remaining settings. Use `-Prompt` to force the setup wizard when other parameters are also
supplied, or `-NoPrompt` to suppress it. Explicit parameters and non-interactive/CI runs do not
prompt and must provide `-ApiDirectory`.

Run the guided setup, select the API location, and start the default read-only workload with a
base concurrency of 30 and no time limit:

```powershell
.\scripts\run-loadgen.ps1
```

For an unattended or fully parameterized run, provide the directory containing
`TicketingApi.csproj` explicitly:

```powershell
.\scripts\run-loadgen.ps1 -ApiDirectory C:\work\ticketing-scenario\TicketingApi
```

`-ApiDirectory` accepts an absolute path or a path relative to the repository root and must contain
`TicketingApi.csproj`. To switch implementations, stop LoadGen and the current API, start the other
API, then run the launcher again with that API directory:

```powershell
.\scripts\run-loadgen.ps1 -ApiDirectory .\examples\after\TicketingApi
```

The optional random seed initializes LoadGen's pseudorandom request selection. Reusing a seed such
as `-Seed 42` repeats the same sequence of selected request types, event IDs, customer IDs, and
cities, which makes comparisons more consistent. Concurrency and response timing can still change
the exact completion order. The seed does not create or modify Cosmos DB data; omit it to generate
a new seed, which LoadGen prints at startup.

The Read workload repeatedly exercises these query shapes:

- Event point read (`event-00001`) as the low-cost control
- Upcoming events
- Events in Memphis
- Orders for `customer-00001`
- Orders for hot event `event-00001`

At startup, LoadGen sends one request for every operation in the selected workload before it
begins weighted traffic. Comparison therefore always hits all five read operations and displays
only those five rows. Mixed always hits and displays all seven read/write operations. A run
fails if any displayed operation has no successful request.

Every half second, the terminal prints one cumulative row per operation. Compact dashboard columns
mean:

- `operation` - API workload operation
- `scope` - observed Cosmos query scope, described below
- `act` - requests currently in flight
- `sent` - requests launched since the run or last counter reset
- `ok` - successful HTTP 2xx responses
- `429` - HTTP 429 responses remaining after Cosmos SDK retries
- `err` - other HTTP 4xx/5xx responses plus timeouts and network failures
- `RU/q` - average request charge among responses that supplied `x-ms-request-charge`
- `p95` - cumulative 95th-percentile response latency in milliseconds

The header's `total RU` is the sum of all observed request charges. Cumulative values remain useful
while slow requests are still active. The wide final report separates other 4xx, 5xx, and network
errors. Keep the workload, seed, concurrency, and report interval identical when comparing targets.

The `scope` column reports how the API executed each Cosmos operation:

- `XPK` - query fanned out across physical partitions
- `1PK` - query was routed to one logical partition
- `POINT` - point read using item ID and partition key
- `N/A` - operation is not a query, such as a write
- `MIXED` - the operation reported more than one scope during the run
- `?` - the API did not return scope metadata

LoadGen reads this from the API's `x-cosmos-query-scope` response header; it does not infer scope
from route names or RU charges. In the current `/id` example implementation, upcoming events,
events by city, orders by customer, and orders by event report `XPK`; event detail reports
`POINT`; writes report `N/A`. If a query supplies `QueryRequestOptions.PartitionKey`, the example
repository reports `1PK` automatically.

## Expected throttling and errors

With the current `/id` partition key, four read operations are cross-partition queries and are the
most likely to consume enough RU to trigger throttling:

- `hot-event orders` can fan out and return tens of thousands of orders
- `upcoming query` fans out and performs `ORDER BY`
- `city query` fans out and performs `ORDER BY`
- `customer orders` fans out and performs `ORDER BY`

High concurrency multiplies these costs. Cosmos DB first returns 429 internally and the SDK retries
transient throttles; LoadGen counts a 429 only when retries are exhausted and the API returns it.
Other dashboard errors can include:

- `no rsp` in an RU column - timeout or network failure produced no HTTP response charge
- HTTP 409 - a Mixed workload purchase requested more seats than remained available
- other HTTP 4xx - authentication, authorization, validation, or route failures
- HTTP 5xx - unhandled API or downstream failures
- network errors - connection failure, request timeout, or the API stopping under load

`N/A` under `scope` is not an error. It identifies a write or another non-query operation for which
cross-partition query classification does not apply.

The dashboard header shows total consumed RU, current concurrency, active requests, elapsed and
target time, and running/paused state. Interactive controls are available while it runs:

- `H` - open or close in-dashboard help for every header, scope value, and operation; traffic continues
- `Space` - pause or resume launching requests; elapsed workload time pauses too
- `+` / `-` - increase or decrease concurrency by one
- `R` - reset counters and elapsed time without cancelling requests already in flight
- `T` - cycle target time through 30 seconds, 1 minute, 5 minutes, 15 minutes, and unlimited
- `Q` - stop launching requests, drain in-flight work, and print final totals

The launcher defaults to 30 concurrent requests and unlimited time. OpenAPI discovery remains a
blocking preflight: LoadGen will not start unless all routes required by the selected workload are
present in `/openapi/v1.json`.

## Generate 429 throttling

Use opt-in adaptive saturation when the goal is to find the point where the API returns HTTP 429:

```powershell
.\scripts\run-loadgen.ps1 -Workload Read -Concurrency 4 -Saturate -Duration 120
```

Saturation starts at the requested concurrency and doubles pressure at each dashboard refresh
until LoadGen observes a 429 or reaches the 4,000-request cap. It then holds that pressure for the
rest of the run. In Mixed mode, adaptive saturation replaces the normal timed burst multiplier so
the two mechanisms do not compound. Without `-Saturate`, scheduling is unchanged.

The Cosmos SDK retries transient throttles internally. The example APIs map a 429 that remains
after SDK retries to HTTP 429, including `Retry-After`, so LoadGen can count it. Saturation is
deliberately disruptive and can consume substantial request units; use the Read workload against
a non-production environment unless write pressure is specifically required.

P95 uses bounded, fine-grained histogram buckets with approximately 5% precision. A high p95 is
not clamped: it means the API response was genuinely slow, which is expected for expensive
unbounded queries and is useful when comparing implementations.
The launcher defaults to a 120-second HTTP request timeout. Override it from 1 to 600 seconds,
for example `-RequestTimeout 300`. In RU columns, `-` means no request completed during the
interval, while `no rsp` means a timeout or network failure completed without an HTTP response,
so no `x-ms-request-charge` header was available. Cumulative RU remains visible when earlier
requests for that operation returned charges. `>=600s` is the terminal latency overflow bucket.

The hot-event endpoint can return tens of thousands of orders. At concurrency 50, many large
responses can run simultaneously and exceed a generous timeout. Use the same concurrency for
each target in a comparison; concurrency 10 is a stable starting point for latency measurements,
while higher values are useful when deliberately testing saturation.
In an interactive terminal, LoadGen uses a full-screen alternate buffer and refreshes the same
dashboard like `top`; your normal terminal view returns when LoadGen stops. Output redirected
to a file or CI log remains line-oriented. Only one LoadGen instance can run at a time.

Example reproducible comparison run:

```powershell
.\scripts\run-loadgen.ps1 -Workload Read -Concurrency 10 -Seed 42 -ReportInterval 0.5 -Duration 60
```

For localhost, the launcher automatically creates a development token when none is supplied.
The Read workload requests `Ticketing.Read`; the Mixed workload also requests
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
concurrency. The Read workload remains at constant concurrency to reduce measurement
noise. Both profiles discover routes from OpenAPI and print final per-operation totals on exit.

Omit `-Duration` for an unlimited run. Bounded runs stop launching requests at the requested
time, drain requests already in flight, and then print final totals.

## Review completed summaries

After a run stops gracefully and drains in-flight requests, LoadGen writes the exact final summary
table to a timestamped `.log` file and prints its absolute path. The PowerShell launcher stores logs
under `logs/loadgen` at the repository root by default:

```text
loadgen: summary log: C:\work\ticketapi\logs\loadgen\20260812-153045-123-loadgen-....log
```

Choose another location through the script with `-LogDirectory`. Relative paths are resolved from
the repository root:

```powershell
.\scripts\run-loadgen.ps1 -LogDirectory .\benchmark-results
```

When invoking the LoadGen project directly, set `LOADGEN_LOG_DIRECTORY`; otherwise logs are written
to `logs/loadgen` under the current working directory. The repository's default `logs` directory is
ignored by Git; custom locations are caller-managed. A hard process termination cannot write a
completion summary, so use `Q`, Ctrl+C, or a bounded `-Duration` when a retained result is required.

## Test LoadGen

Run the isolated LoadGen suite before collecting comparison results:

```powershell
dotnet test .\tests\LoadGen.Tests\LoadGen.Tests.csproj
```

The suite uses a local fake ticketing server and does not access Cosmos DB. It covers argument
validation, required and arbitrary API directories, both traffic profiles,
guaranteed execution and success gating for every selected operation, all seven operation types,
OpenAPI discovery failures, bearer and idempotency headers, request bodies, status/RU/latency
metrics, p95 precision, single-instance locking, bounded shutdown, and narrow/wide dashboard
rendering.

The fake-server tests validate LoadGen itself. Run each captured API separately with the same
bounded command to validate its live Cosmos DB behavior and collect comparable RU and latency
results.
