using System.Diagnostics;

namespace LoadGen.Tests;

public sealed class LauncherProcessTests
{
    [Theory]
    [InlineData(null, "root")]
    [InlineData("examples/before/TicketingApi", "before")]
    [InlineData("examples/after/TicketingApi", "after")]
    public async Task Launcher_propagates_directory_label_duration_and_token(
        string? relativeApiDirectory,
        string expectedLabel)
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
        if (relativeApiDirectory is not null)
        {
            startInfo.ArgumentList.Add("-ApiDirectory");
            startInfo.ArgumentList.Add(Path.Combine(
                repositoryRoot,
                relativeApiDirectory.Replace('/', Path.DirectorySeparatorChar)));
        }
        startInfo.ArgumentList.Add("-AccessToken");
        startInfo.ArgumentList.Add("launcher-token");
        startInfo.ArgumentList.Add("-BaseUrl");
        startInfo.ArgumentList.Add(server.BaseUrl);
        startInfo.ArgumentList.Add("-Workload");
        startInfo.ArgumentList.Add("Comparison");
        startInfo.ArgumentList.Add("-Concurrency");
        startInfo.ArgumentList.Add("2");
        startInfo.ArgumentList.Add("-Duration");
        startInfo.ArgumentList.Add("0.6");
        startInfo.ArgumentList.Add("-ReportInterval");
        startInfo.ArgumentList.Add("0.5");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the LoadGen launcher.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await process.WaitForExitAsync(timeout.Token);

        var output = await standardOutput;
    var error = await standardError;
    Assert.True(process.ExitCode == 0, error);
        Assert.Contains($"Starting run '{expectedLabel}'", output);
        Assert.Contains($"run={expectedLabel}", output);
        Assert.Contains("duration=0.6s", output);
        Assert.Contains("request-timeout=120s", output);
        Assert.All(
            server.Requests,
            request => Assert.Equal("Bearer launcher-token", request.Authorization));
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