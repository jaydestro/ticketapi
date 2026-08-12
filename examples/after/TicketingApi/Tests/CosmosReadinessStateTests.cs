using TicketingApi.Cosmos;

namespace TicketingApi.Tests;

public sealed class CosmosReadinessStateTests
{
    [Fact]
    public void RequiresSchemaAndChangeFeedWithoutFailure()
    {
        var state = new CosmosReadinessState { SchemaReady = true, ChangeFeedReady = true };

        Assert.True(state.IsReady);

        state.Failure = "failed";

        Assert.False(state.IsReady);
    }
}