```
Run the seeder against my Cosmos DB account and tell me when it's done.

When it finishes, confirm it actually worked: document counts in both containers, and verify the skew landed - the championship final should be 
sitting on 40,000+ orders while a typical event has only a handful. Report 
total RU consumed and how long it took.


### Optional
If you need it to start clean first:

Wipe the events and orders containers, then run the seeder from scratch and verify the counts and the skew when it's done.
```