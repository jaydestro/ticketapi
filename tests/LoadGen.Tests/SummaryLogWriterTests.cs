namespace LoadGen.Tests;

public sealed class SummaryLogWriterTests
{
    [Fact]
    public async Task Writer_creates_timestamped_log_with_exact_lines()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"loadgen-summary-{Guid.NewGuid():N}");
        try
        {
            var path = await LoadGenSummaryLogWriter.WriteAsync(
                directory,
                ["final line one", "final line two"],
                new DateTimeOffset(2026, 8, 12, 15, 30, 45, 123, TimeSpan.Zero));

            Assert.StartsWith(
                "20260812-153045-123-loadgen-",
                Path.GetFileName(path),
                StringComparison.Ordinal);
            Assert.Equal(["final line one", "final line two"], await File.ReadAllLinesAsync(path));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}