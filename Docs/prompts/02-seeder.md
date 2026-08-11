# Seed the Ticketing Data

```text
Seed the Cosmos DB account configured in the root `appsettings.json` using the existing
Seeder project and the current Entra identity.

1. Confirm that Seeder targets the configured events and orders containers with `/id`
   partition keys and uses
   `DefaultAzureCredential` because `AccountEndpoint` is set and `ConnectionString` is empty.

2. Build the Seeder. Fix only defects that prevent it from compiling or using the shared
   configuration; do not redesign its models or generated dataset.

3. Before writing data, show me the account endpoint, database, container names, active Entra
   identity, and expected scope: 5,000 events plus 250,000 orders. Do not display credentials.
   Ask for confirmation before continuing because this is a live bulk write.

4. After confirmation, run the Seeder once and capture its final document count, total RU
   charge, elapsed time, and any failed writes. The Seeder uses deterministic IDs and upserts,
   so reruns must not increase the expected document counts.

5. Query Cosmos DB to verify:
   - Events container: exactly 5,000 documents
   - Orders container: exactly 250,000 documents
   - Event `event-00001` is the Championship Final
   - Exactly 42,000 orders reference `event-00001`
   - A sampled tail event has far fewer orders than the championship event

   Use parameterized queries and report the actual values. Treat any mismatch or failed write
   as a failed seed operation; investigate and rerun the relevant validation before declaring
   success.

6. Report the target, counts, skew results, Seeder RU charge, elapsed time, and failed writes.

Do not delete documents, wipe or recreate containers, change throughput, assign roles, or
modify Azure resources. Stop and explain the blocker if configuration, resource access, or
RBAC is incomplete.
```
