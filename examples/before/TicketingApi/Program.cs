using Azure.Identity;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;
using TicketingApi.Configuration;
using TicketingApi.Repositories;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls("http://localhost:5107");
builder.Services.AddControllers();
builder.Services.AddOpenApi();

builder.Services
    .AddOptions<CosmosDbOptions>()
    .Bind(builder.Configuration.GetSection(CosmosDbOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton(serviceProvider =>
{
    var options = serviceProvider.GetRequiredService<IOptions<CosmosDbOptions>>().Value;
    var clientOptions = new CosmosClientOptions
    {
        UseSystemTextJsonSerializerWithOptions = new System.Text.Json.JsonSerializerOptions(
            System.Text.Json.JsonSerializerDefaults.Web)
    };

    return new CosmosClient(
        options.AccountEndpoint,
        new DefaultAzureCredential(),
        clientOptions);
});

builder.Services.AddSingleton<ITicketingRepository, TicketingRepository>();

var app = builder.Build();

app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (CosmosException exception) when (exception.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
    {
        context.Response.Clear();
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.Headers["x-ms-request-charge"] = exception.RequestCharge.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        if (exception.Data[CosmosQueryScopes.ExceptionDataKey] is string queryScope)
        {
            context.Response.Headers["x-cosmos-query-scope"] = queryScope;
        }
        if (exception.RetryAfter is { } retryAfter && retryAfter > TimeSpan.Zero)
        {
            context.Response.Headers.RetryAfter = Math.Ceiling(retryAfter.TotalSeconds)
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }
});

app.MapOpenApi();
app.MapControllers();

app.Run();

public partial class Program;