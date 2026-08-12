using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

internal static class LoadGenSummaryLogWriter
{
    private const string LogDirectoryEnvironmentVariable = "LOADGEN_LOG_DIRECTORY";

    [ModuleInitializer]
    public static void Initialize()
    {
        var originalOutput = Console.Out;
        var capture = new FinalReportCaptureWriter(originalOutput);
        Console.SetOut(capture);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            var lines = capture.GetFinalReport();
            if (lines.Count == 0)
            {
                return;
            }

            try
            {
                var directory = Environment.GetEnvironmentVariable(LogDirectoryEnvironmentVariable);
                if (string.IsNullOrWhiteSpace(directory))
                {
                    directory = Path.Combine(Environment.CurrentDirectory, "logs", "loadgen");
                }

                var path = WriteAsync(directory, lines).GetAwaiter().GetResult();
                originalOutput.WriteLine($"loadgen: summary log: {path}");
                originalOutput.Flush();
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"loadgen: could not write summary log ({exception.Message}).");
            }
        };
    }

    public static async Task<string> WriteAsync(
        string directory,
        IReadOnlyList<string> lines,
        DateTimeOffset? completedAt = null)
    {
        var fullDirectory = Path.GetFullPath(directory);
        Directory.CreateDirectory(fullDirectory);

        var timestamp = (completedAt ?? DateTimeOffset.UtcNow)
            .UtcDateTime
            .ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        var fileName = $"{timestamp}-loadgen-{Guid.NewGuid():N}.log";
        var path = Path.Combine(fullDirectory, fileName);
        await File.WriteAllLinesAsync(path, lines);
        return path;
    }

    private sealed class FinalReportCaptureWriter(TextWriter inner) : TextWriter
    {
        private readonly object _sync = new();
        private readonly List<string> _finalReport = [];
        private bool _capturing;

        public override Encoding Encoding => inner.Encoding;

        public override void Write(char value) => inner.Write(value);

        public override void Write(string? value) => inner.Write(value);

        public override void WriteLine(string? value)
        {
            inner.WriteLine(value);
            lock (_sync)
            {
                if (value?.StartsWith("=== final:", StringComparison.Ordinal) == true)
                {
                    _capturing = true;
                }

                if (_capturing)
                {
                    _finalReport.Add(value ?? string.Empty);
                }
            }
        }

        public override void Flush() => inner.Flush();

        public IReadOnlyList<string> GetFinalReport()
        {
            lock (_sync)
            {
                return [.. _finalReport];
            }
        }
    }
}