# Examples

Captured output of [prompt 03](../Docs/prompts/03-app-build.md), run twice under different
conditions. The prompt text is identical in both runs; only the presence of the Azure Cosmos DB
agent skill changes.

| Directory | Condition |
| --- | --- |
| [before/](before/) | Prompt 03 run with **no** Cosmos DB agent skill installed |
| [after/](after/) | Prompt 03 run with the Cosmos DB agent skill installed |

This is why the prompts describe *what* to build rather than *how*. The latitude they leave is
what the skill fills in, and the difference between these two directories is the result.

## Capturing a run

1. Set the skill state for the run you are capturing.

   Removed:

   ```powershell
   Remove-Item .agents, .claude -Recurse -Force -ErrorAction SilentlyContinue
   Remove-Item skills-lock.json -Force -ErrorAction SilentlyContinue
   ```

   Installed:

   ```powershell
   npx skills add AzureCosmosDB/cosmosdb-agent-kit
   ```

2. Start a fresh agent session so the skill change takes effect.
3. Run prompt 03 as written, with prompts 01 and 02 already complete.
4. Copy the generated project into the matching directory:

   ```powershell
   Copy-Item .\TicketingApi -Destination .\examples\before\TicketingApi -Recurse
   ```

Keep everything else constant between runs: the same prompt text, the same provisioned Cosmos DB
account, and the same seeded data.

## Comparing the two

Run LoadGen against each build with identical parameters so the request-unit and latency numbers
are comparable. Start one API at a time, then identify its captured project directory to the
launcher:

```powershell
.\scripts\run-loadgen.ps1 -ApiDirectory .\examples\before\TicketingApi -RunLabel "Before fix" -Workload Read -Concurrency 10 -Seed 42 -ReportInterval 0.5 -Duration 60
.\scripts\run-loadgen.ps1 -ApiDirectory .\examples\after\TicketingApi -RunLabel "After fix" -Workload Read -Concurrency 10 -Seed 42 -ReportInterval 0.5 -Duration 60
```

`-RunLabel` gives each example an explicit label in the live dashboard and final report, so saved
output remains attributable without teaching LoadGen about this repository's naming convention.

The Read workload is read-only, so measuring does not modify seeded data.

## What is not captured here

`bin/`, `obj/`, and `appsettings.json` are ignored repository-wide, so build output stays out of
these directories and no account endpoint or environment value is committed with a sample.
