using TicketingApi.Configuration;

namespace TicketingApi.Tests;

public sealed class CosmosDbOptionsValidatorTests
{
    private readonly CosmosDbOptionsValidator _validator = new();

    [Fact]
    public void ValidatesEndpointAuthentication()
    {
        var result = _validator.Validate(null, ValidOptions());

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void RejectsMissingOrAmbiguousAuthentication()
    {
        var missing = ValidOptions(accountEndpoint: string.Empty);
        var ambiguous = ValidOptions(connectionString: "AccountEndpoint=https://localhost:8081/;AccountKey=test;");

        Assert.True(_validator.Validate(null, missing).Failed);
        Assert.True(_validator.Validate(null, ambiguous).Failed);
    }

    [Fact]
    public void RejectsDuplicateContainerNames()
    {
        var options = ValidOptions(leaseContainerName: "ticketing-write");

        var result = _validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("must be distinct", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
    }

    private static CosmosDbOptions ValidOptions(
        string accountEndpoint = "https://tickets.documents.azure.com/",
        string connectionString = "",
        string leaseContainerName = "change-feed-leases") => new()
    {
        AccountEndpoint = accountEndpoint,
        ConnectionString = connectionString,
        DatabaseName = "ticketing",
        LeaseContainerName = leaseContainerName
    };
}