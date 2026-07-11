using ResourceAlerter.Data;
using ScottPlot;

namespace ResourceAlerter.Reporting;

/// <summary>
/// Renders a time-series line/area chart to JPEG bytes, with red vertical lines marking alert
/// starts. ScottPlot 5 renders via SkiaSharp, so this works headless inside the service (no
/// GUI/message pump needed) — the Viewer app draws the same style of chart interactively.
/// </summary>
public static class ChartRenderer
{
    public static byte[] RenderSeriesJpeg(
        string title,
        string? unit,
        IReadOnlyList<SamplePoint> samples,
        IEnumerable<DateTimeOffset> alertStarts,
        int width = 900,
        int height = 350)
    {
        var plot = new Plot();

        if (samples.Count > 0)
        {
            var xs = samples.Select(s => s.Timestamp.LocalDateTime.ToOADate()).ToArray();
            var ys = samples.Select(s => s.Value).ToArray();

            var scatter = plot.Add.Scatter(xs, ys);
            scatter.MarkerSize = 0;
            scatter.LineWidth = 1.5f;
            scatter.Color = new Color(31, 119, 180);
            scatter.FillY = true;
            scatter.FillYColor = scatter.Color.WithAlpha(0.15);
            scatter.FillYValue = ys.Min();
        }

        foreach (var alertStart in alertStarts)
        {
            var line = plot.Add.VerticalLine(alertStart.LocalDateTime.ToOADate());
            line.Color = Colors.Red;
            line.LineWidth = 1.5f;
        }

        plot.Axes.DateTimeTicksBottom();
        plot.Title(title);
        if (!string.IsNullOrEmpty(unit))
        {
            plot.YLabel(unit);
        }

        return plot.GetImageBytes(width, height, ImageFormat.Jpeg);
    }
}
