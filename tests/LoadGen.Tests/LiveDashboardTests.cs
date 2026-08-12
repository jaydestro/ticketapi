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
        Assert.Contains("\u001b[2J\u001b[H\u001b[1;36mshort", rendered);
        Assert.Contains("\u001b[1;36m", rendered);
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

    [Fact]
    public void Interactive_dashboard_colors_scope_rows_from_named_metric_columns()
    {
        using var output = new StringWriter();
        var dashboard = new LiveDashboard(
            interactive: true,
            output: output,
            widthProvider: () => 80);

        dashboard.Render([
            "header",
            "summary",
            "operation          scope act sent    ok  429  err    RU/q     p95",
            "upcoming query       XPK   0    10     9    1    0    2.50      10",
            "hot-event orders     XPK   0    10     9    0    1    2.50      10"
        ]);

        var rendered = output.ToString();
        Assert.Contains("\u001b[33mupcoming query", rendered);
        Assert.Contains("\u001b[31mhot-event orders", rendered);
    }

    [Fact]
    public void Interactive_dashboard_colors_rows_with_grouped_metric_counts()
    {
        using var output = new StringWriter();
        var dashboard = new LiveDashboard(
            interactive: true,
            output: output,
            widthProvider: () => 120);

        dashboard.Render([
            "header",
            "summary",
            "operation          scope act  sent     ok   429  err",
            "upcoming query       XPK   0 1,001  1,000     1    0"
        ]);

        Assert.Contains("\u001b[33mupcoming query", output.ToString());
    }

    private static int Count(string value, string target) =>
        (value.Length - value.Replace(target, string.Empty, StringComparison.Ordinal).Length) / target.Length;
}