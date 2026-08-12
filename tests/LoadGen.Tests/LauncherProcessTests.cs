using System.Diagnostics;

namespace LoadGen.Tests;

public sealed class LauncherProcessTests
{
    [Fact]
    public async Task Launcher_propagates_explicit_directory_duration_and_token()
    {
        await using var server = new FakeTicketingServer(includeWriteRoutes: false);
        var repositoryRoot = FindRepositoryRoot();
        var startInfo = new ProcessStartInfo("pwsh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = repositoryRoot
        };
        startInfo.Environment["TICKETING_LOADGEN_LOCK_PATH"] = Path.Combine(
            Path.GetTempPath(),
            $"ticketapi-loadgen-launcher-tests-{Environment.ProcessId}.lock");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(Path.Combine(repositoryRoot, "scripts", "run-loadgen.ps1"));
        var apiDirectory = Path.Combine(repositoryRoot, "examples", "after", "TicketingApi");
        startInfo.ArgumentList.Add("-ApiDirectory");
        startInfo.ArgumentList.Add(apiDirectory);
        startInfo.ArgumentList.Add("-AccessToken");
        startInfo.ArgumentList.Add("launcher-token");
        startInfo.ArgumentList.Add("-BaseUrl");
        startInfo.ArgumentList.Add(server.BaseUrl);
        startInfo.ArgumentList.Add("-Workload");
        startInfo.ArgumentList.Add("Read");
        startInfo.ArgumentList.Add("-Concurrency");
        startInfo.ArgumentList.Add("2");
        startInfo.ArgumentList.Add("-Duration");
        startInfo.ArgumentList.Add("0.6");
        startInfo.ArgumentList.Add("-ReportInterval");
        startInfo.ArgumentList.Add("0.5");
        startInfo.ArgumentList.Add("-Saturate");
        var logDirectory = Path.Combine(Path.GetTempPath(), $"loadgen-launcher-log-{Guid.NewGuid():N}");
        startInfo.ArgumentList.Add("-LogDirectory");
        startInfo.ArgumentList.Add(logDirectory);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the LoadGen launcher.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await process.WaitForExitAsync(timeout.Token);

        var output = await standardOutput;
        var error = await standardError;
        Assert.True(process.ExitCode == 0, error);
        Assert.Contains("Starting Read LoadGen", output);
        Assert.Contains(apiDirectory, output);
        Assert.Contains("for 0.6 seconds", output);
        Assert.Contains("request-timeout=120s", output);
        Assert.Contains("saturation=adaptive", output);
        Assert.Contains($"Summary log directory: {logDirectory}", output);
        Assert.Single(Directory.GetFiles(logDirectory, "*.log"));
        Assert.All(
            server.Requests,
            request => Assert.Equal("Bearer launcher-token", request.Authorization));
    }

    [Fact]
    public async Task Launcher_defaults_to_30_concurrent_requests_and_unlimited_time()
    {
        await using var server = new FakeTicketingServer(includeWriteRoutes: false);
        var repositoryRoot = FindRepositoryRoot();
        var startInfo = new ProcessStartInfo("pwsh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = repositoryRoot
        };
        startInfo.Environment["TICKETING_LOADGEN_LOCK_PATH"] = Path.Combine(
            Path.GetTempPath(),
            $"ticketapi-loadgen-defaults-{Guid.NewGuid():N}.lock");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(Path.Combine(repositoryRoot, "scripts", "run-loadgen.ps1"));
        startInfo.ArgumentList.Add("-ApiDirectory");
        startInfo.ArgumentList.Add(Path.Combine(repositoryRoot, "examples", "after", "TicketingApi"));
        startInfo.ArgumentList.Add("-AccessToken");
        startInfo.ArgumentList.Add("launcher-token");
        startInfo.ArgumentList.Add("-BaseUrl");
        startInfo.ArgumentList.Add(server.BaseUrl);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the LoadGen launcher.");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            string? startupLine = null;
            while (!timeout.IsCancellationRequested && startupLine is null)
            {
                var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
                if (line is null)
                {
                    break;
                }

                if (line.StartsWith("Starting Read LoadGen", StringComparison.Ordinal))
                {
                    startupLine = line;
                }
            }

            Assert.NotNull(startupLine);
            Assert.Contains("base concurrency 30", startupLine);
            Assert.Contains("for until Ctrl+C", startupLine);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    [Fact]
    public async Task Noninteractive_launcher_requires_api_directory()
    {
        var repositoryRoot = FindRepositoryRoot();
        var startInfo = new ProcessStartInfo("pwsh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = repositoryRoot
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(Path.Combine(repositoryRoot, "scripts", "run-loadgen.ps1"));
        startInfo.ArgumentList.Add("-AccessToken");
        startInfo.ArgumentList.Add("launcher-token");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the LoadGen launcher.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);

        Assert.NotEqual(0, process.ExitCode);
        Assert.Contains("ApiDirectory is required", await error);
        Assert.DoesNotContain("Starting", await output);
    }

    [Fact]
    public async Task Prompted_launcher_accepts_validated_settings_and_starts()
    {
        await using var server = new FakeTicketingServer(includeWriteRoutes: false);
        var repositoryRoot = FindRepositoryRoot();
        var startInfo = new ProcessStartInfo("pwsh")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = repositoryRoot
        };
        startInfo.Environment["TICKETING_LOADGEN_LOCK_PATH"] = Path.Combine(
            Path.GetTempPath(),
            $"ticketapi-loadgen-prompt-{Guid.NewGuid():N}.lock");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(Path.Combine(repositoryRoot, "scripts", "run-loadgen.ps1"));
        startInfo.ArgumentList.Add("-Prompt");
        startInfo.ArgumentList.Add("-AccessToken");
        startInfo.ArgumentList.Add("launcher-token");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the prompted LoadGen launcher.");
        await process.StandardInput.WriteLineAsync("examples/after/TicketingApi");
        await process.StandardInput.WriteLineAsync(string.Empty);
        await process.StandardInput.WriteLineAsync("2");
        await process.StandardInput.WriteLineAsync("y");
        await process.StandardInput.WriteLineAsync("0.6");
        await process.StandardInput.WriteLineAsync("120");
        await process.StandardInput.WriteLineAsync(server.BaseUrl);
        await process.StandardInput.WriteLineAsync("0.5");
        await process.StandardInput.WriteLineAsync(string.Empty);
        await process.StandardInput.WriteLineAsync("y");
        process.StandardInput.Close();

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await process.WaitForExitAsync(timeout.Token);
        var output = await outputTask;
        var error = await errorTask;

        Assert.True(process.ExitCode == 0, error);
        Assert.Contains("Ticketing LoadGen setup", output);
        Assert.Contains("Selected settings", output);
        Assert.Contains("Concurrency: 2", output);
        Assert.Contains("Saturation:  adaptive", output);
        Assert.Contains("Duration:    0.6 seconds", output);
        Assert.Contains("Starting Read LoadGen", output);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "scripts", "run-loadgen.ps1")) &&
                File.Exists(Path.Combine(directory.FullName, "LoadGen", "LoadGen.csproj")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the ticketapi repository root.");
    }
}