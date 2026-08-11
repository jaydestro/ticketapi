# Four-Lens Review Prompts

Run these prompts one at a time. Wait for each review and its fixes to finish before running the next prompt; do not combine the lenses into one request.

## Data model & partition key

Will this data model hold up at scale?

## RU efficiency

Why are these queries so expensive?

## Indexing

What is our indexing policy costing us?

## SDK & maintainability

Is this ready for production?

## Repair

Review this whole thing for production readiness and fix what you find.  Focus on code correctness and less on infrastructure.  Use Azure Cosmos DB best practices.  Skip LoadGen and Seeder, those are supportive apps.
