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

app.MapOpenApi();
app.MapControllers();

app.Run();

public partial class Program;