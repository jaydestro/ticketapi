namespace LoadGen.Tests;

public sealed class LiveDashboardTests
{
    [Fact]
    public void Interactive_dashboard_clears_each_frame_and_restores_terminal_once()
    {
        using var output = new StringWriter();
        var dashboard = new LiveDashboard(
            interactive: true,
            output: output,
            widthProvider: () => 80);

        dashboard.Render(["first frame"]);
        dashboard.Render(["short"]);
        dashboard.Complete();
        dashboard.Complete();

        var rendered = output.ToString();
        Assert.Equal(80, dashboard.Width);
        Assert.Equal(3, Count(rendered, "\u001b[2J\u001b[H"));
        Assert.Equal(1, Count(rendered, "\u001b[?1049h"));
        Assert.Equal(1, Count(rendered, "\u001b[?1049l"));
        Assert.Contains("\u001b[2J\u001b[Hshort", rendered);
    }

    [Fact]
    public void Redirected_dashboard_writes_plain_lines_without_ansi_sequences()
    {
        using var output = new StringWriter();
        var dashboard = new LiveDashboard(interactive: false, output: output);

        dashboard.Render(["plain output"]);

        Assert.Equal(int.MaxValue, dashboard.Width);
        Assert.Equal("plain output" + Environment.NewLine, output.ToString());
    }

    private static int Count(string value, string target) =>
        (value.Length - value.Replace(target, string.Empty, StringComparison.Ordinal).Length) / target.Length;
}