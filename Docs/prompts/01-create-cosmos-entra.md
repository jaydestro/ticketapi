# Create Cosmos DB and Configure Entra ID

```text
Provision a new Azure Cosmos DB for NoSQL environment and configure this workspace to use it.

Collect these values:

- Azure subscription and location
- New resource group and globally unique Cosmos DB account names
- Database name
- Events and orders container names
- New user-assigned managed identity name

1. Verify the selected Azure account, subscription, location, and identity. Show the planned
   resource names and ask for approval before creating anything.

2. Create reusable subscription-scope Bicep under `infra/` for:
   - A resource group
   - A user-assigned managed identity for the hosted application
   - A serverless Azure Cosmos DB for NoSQL account with local/key authentication disabled
   - One SQL database
   - Events and orders containers, each partitioned by `/id`
   - A data-plane `Cosmos DB Built-in Data Contributor` role assignment for the current
     `az login` user at account scope
    - The same role assignment for the user-assigned managed identity

   Do not configure manual or autoscale throughput. Do not store keys, tokens, or connection
   strings.

3. Validate the Bicep and run an Azure deployment what-if. Present the expected changes and
   ask for approval before deploying. After approval, deploy the resources.

4. Verify that the account, database, containers, `/id` partition keys, disabled local auth,
   and data-plane role assignments all exist. Retrieve the account endpoint from Azure rather
   than constructing it. Allow for normal RBAC propagation, then confirm the current
   `az login` user can perform a read-only data-plane operation against both containers.

5. Create the repository-root `appsettings.json` from `appsettings.json.example`. Keep the
   example file unchanged, then set these values in the generated file:
   - `AccountEndpoint`
   - `ManagedIdentityClientId`
   - `DatabaseName`
   - `EventsContainerName`
   - `OrdersContainerName`

   Preserve the explanatory fields, leave `ConnectionString` empty, and validate the generated
   JSON. Do not create a project-local settings file.

6. Keep the root README focused on the repository goal and what each numbered prompt builds.
   Do not add subscription details, deployed resource names, IDs, endpoints, run results,
   deployment commands, or other environment-specific metadata.
```
