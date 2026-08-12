using System.Diagnostics;

namespace LoadGen.Tests;

internal sealed record LoadGenProcessResult(int ExitCode, string StandardOutput, string StandardError);

internal static class LoadGenProcess
{
    private static readonly string TestLockPath = Path.Combine(
        Path.GetTempPath(),
        $"ticketapi-loadgen-tests-{Environment.ProcessId}.lock");

    public static Task<LoadGenProcessResult> RunAsync(
        string baseUrl,
        string profile,
        double durationSeconds = 0.6,
        int concurrency = 2,
        string? accessToken = null) =>
        RunRawAsync(
        [
            "--concurrency", concurrency.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--duration", durationSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--base-url", baseUrl,
            "--profile", profile,
            "--report-interval", "0.5",
            "--request-timeout", "120",
            "--seed", "42"
        ], accessToken);

    public static async Task<LoadGenProcessResult> RunRawAsync(
        IReadOnlyList<string> arguments,
        string? accessToken = null,
        string? logDirectory = null)
    {
        using var process = Start(arguments, accessToken, logDirectory);
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("LoadGen did not exit within 20 seconds.");
        }

        return new LoadGenProcessResult(
            process.ExitCode,
            await standardOutput,
            await standardError);
    }

    public static Process Start(
        IReadOnlyList<string> arguments,
        string? accessToken = null,
        string? logDirectory = null)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(typeof(MetricsCollector).Assembly.Location);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment.Remove("TICKETING_API_ACCESS_TOKEN");
        startInfo.Environment["TICKETING_LOADGEN_LOCK_PATH"] = TestLockPath;
        startInfo.Environment["LOADGEN_LOG_DIRECTORY"] = logDirectory ?? Path.Combine(
            Path.GetTempPath(),
            $"ticketapi-loadgen-test-logs-{Environment.ProcessId}");
        if (accessToken is not null)
        {
            startInfo.Environment["TICKETING_API_ACCESS_TOKEN"] = accessToken;
        }

        return Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start LoadGen.");
    }
}