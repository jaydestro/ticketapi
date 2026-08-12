using System.Text.Json;

namespace LoadGen.Tests;

public sealed class LoadGenProcessTests
{
    public static TheoryData<string[]> InvalidArguments => new()
    {
        new[] { "--concurrency", "0" },
        new[] { "--concurrency", "4001" },
        new[] { "--concurrency", "1", "--duration", "0" },
        new[] { "--concurrency", "1", "--duration", "NaN" },
        new[] { "--concurrency", "1", "--base-url", "ftp://localhost" },
        new[] { "--concurrency", "1", "--profile", "unknown" },
        new[] { "--concurrency", "1", "--report-interval", "0.4" },
        new[] { "--concurrency", "1", "--request-timeout", "0" },
        new[] { "--concurrency", "1", "--request-timeout", "601" },
        new[] { "--concurrency", "1", "--run-label", "obsolete" },
        new[] { "--concurrency", "1", "--unknown" }
    };

    [Theory]
    [MemberData(nameof(InvalidArguments))]
    public async Task Invalid_arguments_return_usage_error(string[] arguments)
    {
        var result = await LoadGenProcess.RunRawAsync(arguments);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Usage: LoadGen", result.StandardError);
    }

    [Fact]
    public async Task Completed_run_writes_exact_final_summary_and_prints_path()
    {
        var logDirectory = Path.Combine(Path.GetTempPath(), $"loadgen-process-log-{Guid.NewGuid():N}");
        try
        {
            await using var server = new FakeTicketingServer(includeWriteRoutes: false);
            var result = await LoadGenProcess.RunRawAsync(
            [
                "--concurrency", "1",
                "--duration", "0.1",
                "--base-url", server.BaseUrl,
                "--profile", LoadProfiles.Comparison,
                "--report-interval", "0.5"
            ], logDirectory: logDirectory);

            Assert.Equal(0, result.ExitCode);
            var logPath = Assert.Single(Directory.GetFiles(logDirectory, "*.log"));
            Assert.Contains($"loadgen: summary log: {logPath}", result.StandardOutput);
            var log = await File.ReadAllTextAsync(logPath);
            Assert.Contains("=== final: read", log);
            Assert.Contains("Total used RU:", log);
            Assert.Contains("XPK", log);
            Assert.Contains("TOTAL", log);
        }
        finally
        {
            if (Directory.Exists(logDirectory))
            {
                Directory.Delete(logDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Comparison_profile_is_read_only()
    {
        await using var server = new FakeTicketingServer(includeWriteRoutes: false);

        var result = await LoadGenProcess.RunAsync(
            server.BaseUrl,
            LoadProfiles.Comparison,
            durationSeconds: 0.1,
            concurrency: 1,
            accessToken: "integration-token");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("workload=read", result.StandardOutput);
        Assert.Contains("2.50", result.StandardOutput);
        Assert.Contains("POINT", result.StandardOutput);
        Assert.Contains("XPK", result.StandardOutput);
        var traffic = server.Requests.Where(request => request.Path != "/openapi/v1.json").ToArray();
        Assert.NotEmpty(traffic);
        Assert.All(traffic, request => Assert.Equal("GET", request.Method));
        Assert.All(traffic, request => Assert.Equal("Bearer integration-token", request.Authorization));
        Assert.Contains(traffic, request => request.Path == "/api/events/event-00001");
        Assert.Contains(traffic, request => request.Path == "/api/events/city/Memphis");
        Assert.Contains(traffic, request => request.Path == "/api/orders/customer/customer-00001");
        Assert.Contains(traffic, request => request.Path == "/api/orders/event/event-00001");
        Assert.Equal(5, traffic.Select(request => request.Path).Distinct().Count());
    }

    [Fact]
    public async Task Saturation_mode_generates_429s_and_holds_after_observation()
    {
        var requestCount = 0;
        await using var server = new FakeTicketingServer(
            includeWriteRoutes: false,
            apiStatusResolver: _ => Interlocked.Increment(ref requestCount) > 15
                ? FakeTicketingServer.StatusCodes.TooManyRequests
                : FakeTicketingServer.StatusCodes.Ok);

        var result = await LoadGenProcess.RunRawAsync(
        [
            "--concurrency", "1",
            "--duration", "1.1",
            "--base-url", server.BaseUrl,
            "--profile", LoadProfiles.Comparison,
            "--report-interval", "0.5",
            "--request-timeout", "120",
            "--seed", "42",
            "--saturate"
        ]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("saturation=adaptive", result.StandardOutput);
        Assert.Contains("saturation=holding", result.StandardOutput);
        Assert.True(requestCount > 15);
    }

    [Fact]
    public async Task Mixed_profile_exercises_every_operation_and_write_contract()
    {
        await using var server = new FakeTicketingServer();

        var result = await LoadGenProcess.RunAsync(
            server.BaseUrl,
            LoadProfiles.Mixed,
            durationSeconds: 0.1,
            concurrency: 1,
            accessToken: "write-token");

        Assert.Equal(0, result.ExitCode);
        var traffic = server.Requests.Where(request => request.Path != "/openapi/v1.json").ToArray();
        Assert.Contains(traffic, request => request.Method == "GET" && IsEventDetail(request.Path));
        Assert.Contains(traffic, request => request.Method == "GET" && request.Path == "/api/events/upcoming");
        Assert.Contains(traffic, request => request.Method == "GET" && request.Path.StartsWith("/api/events/city/"));
        Assert.Contains(traffic, request => request.Method == "GET" && request.Path.StartsWith("/api/orders/customer/"));
        Assert.Contains(traffic, request => request.Method == "GET" && request.Path.StartsWith("/api/orders/event/"));

        var purchase = Assert.Single(traffic.Where(request => request.Method == "POST" && request.Path == "/api/orders").Take(1));
        Assert.False(string.IsNullOrWhiteSpace(purchase.IdempotencyKey));
        using (var purchaseBody = JsonDocument.Parse(purchase.Body))
        {
            Assert.True(purchaseBody.RootElement.TryGetProperty("eventId", out _));
            Assert.True(purchaseBody.RootElement.TryGetProperty("customerId", out _));
            Assert.InRange(purchaseBody.RootElement.GetProperty("quantity").GetInt32(), 1, 4);
        }

        var createEvent = Assert.Single(traffic.Where(request => request.Method == "POST" && request.Path == "/api/events").Take(1));
        using var eventBody = JsonDocument.Parse(createEvent.Body);
        Assert.True(eventBody.RootElement.TryGetProperty("name", out _));
        Assert.True(eventBody.RootElement.TryGetProperty("eventDate", out _));
        Assert.True(eventBody.RootElement.TryGetProperty("totalSeats", out _));
    }

    [Fact]
    public async Task Mixed_profile_rejects_comparison_only_openapi()
    {
        await using var server = new FakeTicketingServer(includeWriteRoutes: false);

        var result = await LoadGenProcess.RunAsync(server.BaseUrl, LoadProfiles.Mixed);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("missing required mixed route", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("purchase write", result.StandardOutput);
        Assert.Contains("create event", result.StandardOutput);
    }

    [Fact]
    public async Task Unauthorized_openapi_returns_discovery_error()
    {
        await using var server = new FakeTicketingServer(
            openApiStatus: FakeTicketingServer.StatusCodes.Unauthorized);

        var result = await LoadGenProcess.RunAsync(server.BaseUrl, LoadProfiles.Comparison);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("Ticketing.Read access", result.StandardOutput);
    }

    [Fact]
    public async Task All_failed_requests_return_no_success_error()
    {
        await using var server = new FakeTicketingServer(
            apiStatus: FakeTicketingServer.StatusCodes.InternalServerError);

        var result = await LoadGenProcess.RunAsync(server.BaseUrl, LoadProfiles.Comparison);

        Assert.Equal(4, result.ExitCode);
        Assert.Contains("no requests succeeded", result.StandardError);
        Assert.Contains("workload=read", result.StandardOutput);
    }

    [Fact]
    public async Task One_unsuccessful_selected_operation_fails_coverage_gate()
    {
        await using var server = new FakeTicketingServer(
            apiStatusResolver: path => path == "/api/events/upcoming"
                ? FakeTicketingServer.StatusCodes.InternalServerError
                : FakeTicketingServer.StatusCodes.Ok);

        var result = await LoadGenProcess.RunAsync(
            server.BaseUrl,
            LoadProfiles.Comparison,
            durationSeconds: 0.1,
            concurrency: 1);

        Assert.Equal(5, result.ExitCode);
        Assert.Contains("upcoming query", result.StandardError);
    }

    [Fact]
    public async Task Second_process_is_rejected_while_first_holds_instance_lock()
    {
        await using var server = new FakeTicketingServer();
        var first = LoadGenProcess.RunAsync(
            server.BaseUrl,
            LoadProfiles.Comparison,
            durationSeconds: 2);
        await server.WaitForTrafficAsync(TimeSpan.FromSeconds(5));

        var second = await LoadGenProcess.RunAsync(
            server.BaseUrl,
            LoadProfiles.Comparison,
            durationSeconds: 0.2);

        Assert.Equal(3, second.ExitCode);
        Assert.Contains("another LoadGen process", second.StandardError);
        Assert.Equal(0, (await first).ExitCode);
    }

    [Fact]
    public async Task Bearer_token_requires_https_for_non_loopback_target()
    {
        var result = await LoadGenProcess.RunRawAsync(
        [
            "--concurrency", "1",
            "--duration", "0.1",
            "--base-url", "http://example.com"
        ], "token");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("bearer tokens require HTTPS", result.StandardError);
    }

    private static bool IsEventDetail(string path) =>
        path.StartsWith("/api/events/event-", StringComparison.Ordinal) &&
        path.Count(character => character == '/') == 3;
}