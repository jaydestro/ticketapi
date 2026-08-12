using Azure.Identity;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;
using TicketingApi.Configuration;
using TicketingApi.Cosmos;
using TicketingApi.Pagination;
using TicketingApi.Repositories;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();

builder.Services
    .AddOptions<CosmosDbOptions>()
    .Bind(builder.Configuration.GetSection(CosmosDbOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<CosmosDbOptions>, CosmosDbOptionsValidator>();

var authority = builder.Configuration["Authentication:Authority"];
var audience = builder.Configuration["Authentication:Audience"];
var authenticationEnabled = !string.IsNullOrWhiteSpace(authority) && !string.IsNullOrWhiteSpace(audience);
if ((!string.IsNullOrWhiteSpace(authority) || !string.IsNullOrWhiteSpace(audience)) && !authenticationEnabled)
{
    throw new InvalidOperationException("Authentication:Authority and Authentication:Audience must be configured together.");
}
if (!builder.Environment.IsDevelopment() && !authenticationEnabled)
{
    throw new InvalidOperationException("JWT authentication must be configured outside the Development environment.");
}
if (authenticationEnabled)
{
    builder.Services
        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.Authority = authority;
            options.Audience = audience;
        });
    builder.Services.AddAuthorization(options =>
    {
        options.FallbackPolicy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .Build();
    });
}

builder.Services.AddSingleton(serviceProvider =>
{
    var options = serviceProvider.GetRequiredService<IOptions<CosmosDbOptions>>().Value;
    var clientOptions = new CosmosClientOptions
    {
        ApplicationName = "TicketingApi",
        ConnectionMode = IsEmulator(options.ConnectionString)
            ? ConnectionMode.Gateway
            : ConnectionMode.Direct,
        UseSystemTextJsonSerializerWithOptions = new System.Text.Json.JsonSerializerOptions(
            System.Text.Json.JsonSerializerDefaults.Web)
    };

    if (!string.IsNullOrWhiteSpace(options.ConnectionString))
    {
        return new CosmosClient(options.ConnectionString, clientOptions);
    }

    var credentialOptions = new DefaultAzureCredentialOptions();
    if (!string.IsNullOrWhiteSpace(options.ManagedIdentityClientId))
    {
        credentialOptions.ManagedIdentityClientId = options.ManagedIdentityClientId;
    }

    return new CosmosClient(options.AccountEndpoint, new DefaultAzureCredential(credentialOptions), clientOptions);
});

builder.Services.AddSingleton<CosmosReadinessState>();
builder.Services.AddHostedService<CosmosDbInitializer>();
builder.Services.AddHostedService<TicketingChangeFeedWorker>();
builder.Services.AddSingleton<ITicketingRepository, TicketingRepository>();

var app = builder.Build();

app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (InvalidContinuationTokenException exception)
    {
        context.Response.Clear();
        await WriteProblemAsync(
            context,
            StatusCodes.Status400BadRequest,
            "Invalid request",
            exception.Message);
    }
    catch (CosmosException exception)
    {
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("CosmosExceptionMiddleware");
        logger.LogError(
            exception,
            "Cosmos request failed: status={StatusCode}, substatus={SubStatusCode}, activityId={ActivityId}, diagnostics={Diagnostics}",
            exception.StatusCode,
            exception.SubStatusCode,
            exception.ActivityId,
            exception.Diagnostics?.ToString());

        var statusCode = exception.StatusCode switch
        {
            System.Net.HttpStatusCode.BadRequest => StatusCodes.Status400BadRequest,
            System.Net.HttpStatusCode.TooManyRequests => StatusCodes.Status429TooManyRequests,
            System.Net.HttpStatusCode.Conflict => StatusCodes.Status409Conflict,
            System.Net.HttpStatusCode.PreconditionFailed => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status503ServiceUnavailable
        };
        context.Response.Clear();
        context.Response.Headers["x-ms-request-charge"] = exception.RequestCharge.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        context.Response.Headers["x-ms-activity-id"] = exception.ActivityId;
        if (exception.Data[CosmosQueryScopes.ExceptionDataKey] is string queryScope)
        {
            context.Response.Headers["x-cosmos-query-scope"] = queryScope;
        }
        if (exception.RetryAfter is { } retryAfter && retryAfter > TimeSpan.Zero)
        {
            context.Response.Headers.RetryAfter = Math.Ceiling(retryAfter.TotalSeconds)
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        await WriteProblemAsync(
            context,
            statusCode,
            statusCode == StatusCodes.Status429TooManyRequests ? "Request rate limited" : "Database request failed",
            statusCode == StatusCodes.Status429TooManyRequests
                ? "The database is temporarily rate limiting requests. Retry after the indicated delay."
                : "The database could not complete the request.");
    }
    catch (TicketingPersistenceException exception)
    {
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("CosmosExceptionMiddleware");
        logger.LogError(
            exception,
            "Cosmos transactional operation failed: status={StatusCode}, activityId={ActivityId}, requestCharge={RequestCharge}",
            exception.StatusCode,
            exception.ActivityId,
            exception.RequestCharge);

        var statusCode = exception.StatusCode switch
        {
            System.Net.HttpStatusCode.TooManyRequests => StatusCodes.Status429TooManyRequests,
            System.Net.HttpStatusCode.Conflict => StatusCodes.Status409Conflict,
            System.Net.HttpStatusCode.PreconditionFailed => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status503ServiceUnavailable
        };
        context.Response.Clear();
        context.Response.Headers["x-ms-request-charge"] = exception.RequestCharge.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        context.Response.Headers["x-ms-activity-id"] = exception.ActivityId;
        await WriteProblemAsync(
            context,
            statusCode,
            statusCode == StatusCodes.Status429TooManyRequests ? "Request rate limited" : "Database request failed",
            statusCode == StatusCodes.Status429TooManyRequests
                ? "The database is temporarily rate limiting requests. Retry the request."
                : "The database could not complete the request.");
    }
    catch (Exception exception) when (!context.Response.HasStarted)
    {
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("ExceptionMiddleware");
        logger.LogError(exception, "Unhandled request failure");
        context.Response.Clear();
        await WriteProblemAsync(
            context,
            StatusCodes.Status500InternalServerError,
            "Unexpected server error",
            "The server could not complete the request.");
    }
});

if (authenticationEnabled)
{
    app.UseAuthentication();
    app.UseAuthorization();
}

app.MapOpenApi();
app.MapControllers();
app.MapGet("/health/live", () => Results.Ok(new { status = "live" })).AllowAnonymous();
app.MapGet("/health/ready", (CosmosReadinessState readiness) =>
        readiness.IsReady
            ? Results.Ok(new { status = "ready" })
            : Results.Json(
                new { status = "not-ready" },
                statusCode: StatusCodes.Status503ServiceUnavailable))
    .AllowAnonymous();

app.Run();

static bool IsEmulator(string connectionString) =>
    connectionString.Contains("localhost", StringComparison.OrdinalIgnoreCase) ||
    connectionString.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase);

static async Task WriteProblemAsync(HttpContext context, int statusCode, string title, string detail)
{
    context.Response.StatusCode = statusCode;
    context.Response.Headers.CacheControl = "no-store";
    await Results.Problem(statusCode: statusCode, title: title, detail: detail).ExecuteAsync(context);
}

public partial class Program;