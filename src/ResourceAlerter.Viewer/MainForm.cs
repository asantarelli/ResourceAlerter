using System.Diagnostics;
using ResourceAlerter.Localization;
using ScottPlot.WinForms;

namespace ResourceAlerter.Viewer;

/// <summary>
/// Single-window viewer over the service's recorded data: pick a variable, see its
/// last/current value, and a 24-hour area chart with red vertical lines at alert starts.
/// </summary>
public sealed class MainForm : Form
{
    private static readonly TimeSpan AutoRefreshInterval = TimeSpan.FromSeconds(30);

    private readonly DataReader _reader;
    private readonly ComboBox _seriesCombo;
    private readonly ComboBox _rangeCombo;
    private readonly Label _currentValueLabel;
    private readonly Label _autoRefreshLabel;
    private readonly Button _refreshButton;
    private readonly Button _sendSummaryButton;
    private readonly Button _settingsButton;
    private readonly FormsPlot _plot;
    private readonly ToolTip _plotToolTip;
    private readonly System.Windows.Forms.Timer _autoRefreshTimer;
    private readonly System.Windows.Forms.Timer _yRescaleTimer;

    // Backing data for the currently-loaded series, kept around so the Y-rescale-on-zoom timer
    // can recompute Y limits from whatever's currently visible without re-querying the database
    // on every tick.
    private double[] _loadedXs = Array.Empty<double>();
    private double[] _loadedYs = Array.Empty<double>();
    private double _lastSeenXLeft = double.NaN;
    private double _lastSeenXRight = double.NaN;

    public MainForm(DataReader reader)
    {
        _reader = reader;

        Text = Strings.Viewer_Title(Environment.MachineName);
        Width = 1120;
        Height = 600;
        StartPosition = FormStartPosition.CenterScreen;

        try
        {
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        }
        catch
        {
            // Non-fatal cosmetic fallback to the default WinForms icon.
        }

        var topPanel = new Panel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(8) };

        _seriesCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 320,
            Left = 8,
            Top = 9,
        };
        _seriesCombo.SelectedIndexChanged += (_, _) => LoadSelectedSeries();

        _rangeCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 70,
            Left = 336,
            Top = 9,
        };
        _rangeCombo.Items.Add(new TimeRangeOption(Strings.T("1 hora", "1 hour"), TimeSpan.FromHours(1)));
        _rangeCombo.Items.Add(new TimeRangeOption(Strings.T("2 horas", "2 hours"), TimeSpan.FromHours(2)));
        _rangeCombo.Items.Add(new TimeRangeOption(Strings.T("6 horas", "6 hours"), TimeSpan.FromHours(6)));
        _rangeCombo.Items.Add(new TimeRangeOption(Strings.T("12 horas", "12 hours"), TimeSpan.FromHours(12)));
        _rangeCombo.Items.Add(new TimeRangeOption(Strings.T("24 horas", "24 hours"), TimeSpan.FromHours(24)));
        _rangeCombo.SelectedIndex = 4; // 24h -- same as the fixed range this always showed before
        // A range change is a deliberate "look at a different window" action, same as picking a
        // different series, so it resets zoom — call LoadSelectedSeries directly rather than
        // Refresh(), since the series LIST doesn't need reloading, just this series' data.
        _rangeCombo.SelectedIndexChanged += (_, _) => LoadSelectedSeries();

        _refreshButton = new Button { Text = Strings.Viewer_Refresh, Left = 414, Top = 8, Width = 90 };
        _refreshButton.Click += (_, _) => Refresh(fullReload: true); // also picks up newly-recorded series

        _sendSummaryButton = new Button { Text = Strings.Viewer_SendTodaySummary, Left = 512, Top = 8, Width = 160 };
        _sendSummaryButton.Click += async (_, _) => await SendTodaySummaryAsync();

        _currentValueLabel = new Label
        {
            Left = 690,
            Top = 12,
            AutoSize = true,
            Font = new Font(Font.FontFamily, 10, FontStyle.Bold),
            Text = "—",
        };

        _autoRefreshLabel = new Label
        {
            Left = 690,
            Top = 30,
            AutoSize = true,
            Font = new Font(Font.FontFamily, 7.5f, FontStyle.Regular),
            ForeColor = Color.Gray,
            Text = Strings.Viewer_AutoRefreshEvery(AutoRefreshInterval.TotalSeconds),
        };

        // Dock (not Anchor+manual Left) so this doesn't depend on topPanel's width being known
        // at construction time — topPanel isn't even added to the form yet at this point, so an
        // Anchor-based position computed off the form's own Width was landing in the wrong
        // place (that's why this button wasn't visible).
        _settingsButton = new Button
        {
            Text = Strings.Viewer_Settings,
            Width = 110,
            Dock = DockStyle.Right,
        };
        _settingsButton.Click += (_, _) => OpenSettings();

        topPanel.Controls.Add(_seriesCombo);
        topPanel.Controls.Add(_rangeCombo);
        topPanel.Controls.Add(_refreshButton);
        topPanel.Controls.Add(_sendSummaryButton);
        topPanel.Controls.Add(_currentValueLabel);
        topPanel.Controls.Add(_autoRefreshLabel);
        topPanel.Controls.Add(_settingsButton);

        _plot = new FormsPlot { Dock = DockStyle.Fill };

        // ScottPlot's FormsPlot already supports mouse zoom/pan out of the box (scroll wheel to
        // zoom, drag to pan, right-click for a menu with "Auto Axis" to reset the view) — no
        // custom input handling needed. The only thing stopping it from being useful for
        // inspecting an alert was the periodic auto-refresh resetting the zoom every 30s (see
        // LoadSelectedSeries' resetZoom parameter). This tooltip is just so the built-in
        // interactivity is discoverable.
        _plotToolTip = new ToolTip();
        _plotToolTip.SetToolTip(_plot, Strings.Viewer_ChartZoomHint);

        Controls.Add(_plot);
        Controls.Add(topPanel);

        // Keeps the chart current even if nobody touches the window — the whole point of a
        // monitoring viewer is that it stays accurate while just sitting open on a screen.
        _autoRefreshTimer = new System.Windows.Forms.Timer { Interval = (int)AutoRefreshInterval.TotalMilliseconds };
        _autoRefreshTimer.Tick += (_, _) => Refresh(fullReload: false);

        // Zooming in on X (scroll/drag/right-click-rectangle — ScottPlot's built-in gestures)
        // doesn't rescale Y to match, so a zoomed-in view can look flat if the interesting
        // variation is small relative to the full 24h range. Rather than hook mouse events
        // directly (ScottPlot's rendering surface is a child SKControl, and it's not
        // documented/guaranteed which control actually receives raw pointer input), this polls
        // the X-axis limits at a short interval and only does work when they've actually
        // changed — cheap when idle, and works for every gesture (scroll, drag, rectangle-zoom,
        // even the right-click "Auto Axis" reset) uniformly instead of one-by-one.
        _yRescaleTimer = new System.Windows.Forms.Timer { Interval = 250 };
        _yRescaleTimer.Tick += (_, _) => RescaleYToVisibleXIfChanged();

        FormClosed += (_, _) =>
        {
            _autoRefreshTimer.Stop();
            _yRescaleTimer.Stop();
        };

        Load += (_, _) =>
        {
            Refresh(fullReload: true);
            _autoRefreshTimer.Start();
            _yRescaleTimer.Start();
        };
    }

    /// <summary>
    /// If the visible X range has changed since the last check (the user zoomed/panned/reset
    /// via any of ScottPlot's built-in gestures), rescale Y to fit just the data points
    /// currently within that X range, with a little padding — the classic "zoom into a time
    /// window and the value axis auto-scales to make the detail readable" behavior. A no-op
    /// (cheap double comparison) on every tick where nothing changed, i.e. almost always.
    /// </summary>
    private void RescaleYToVisibleXIfChanged()
    {
        if (_loadedXs.Length == 0)
        {
            return;
        }

        var limits = _plot.Plot.Axes.GetLimits();
        if (limits.Left == _lastSeenXLeft && limits.Right == _lastSeenXRight)
        {
            return;
        }

        _lastSeenXLeft = limits.Left;
        _lastSeenXRight = limits.Right;

        double? min = null;
        double? max = null;
        for (var i = 0; i < _loadedXs.Length; i++)
        {
            if (_loadedXs[i] < limits.Left || _loadedXs[i] > limits.Right)
            {
                continue;
            }
            if (min is null || _loadedYs[i] < min)
            {
                min = _loadedYs[i];
            }
            if (max is null || _loadedYs[i] > max)
            {
                max = _loadedYs[i];
            }
        }

        if (min is null || max is null)
        {
            return; // nothing visible in this X range (e.g. panned past the edge of the data)
        }

        var span = max.Value - min.Value;
        var padding = span > 0 ? span * 0.1 : Math.Max(Math.Abs(max.Value) * 0.1, 1);
        _plot.Plot.Axes.SetLimitsY(min.Value - padding, max.Value + padding);
        _plot.Refresh();
    }

    /// <summary>
    /// The Viewer has no SMTP/mail-sending code of its own — it launches
    /// `ResourceAlerter.exe --send-summary` (installed next to it) elevated, which is the exact
    /// same code path the service uses for its 00:00 mail, just run on demand. Elevation matters
    /// here: without it, the hardware-report attachment would come back mostly empty since
    /// LibreHardwareMonitor needs the same access level the service (LocalSystem) has.
    /// </summary>
    private async Task SendTodaySummaryAsync()
    {
        var exePath = Path.Combine(AppContext.BaseDirectory, "ResourceAlerter.exe");
        if (!File.Exists(exePath))
        {
            MessageBox.Show(Strings.Viewer_ExeNotFound(exePath),
                "ResourceAlerter Viewer", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        _sendSummaryButton.Enabled = false;
        _sendSummaryButton.Text = Strings.Viewer_Sending;
        try
        {
            var psi = new ProcessStartInfo(exePath, "--send-summary")
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory,
            };

            using var process = Process.Start(psi) ?? throw new InvalidOperationException(Strings.Viewer_StartProcessFailed);
            await process.WaitForExitAsync();

            if (process.ExitCode == 0)
            {
                MessageBox.Show(Strings.Viewer_SummarySentOk,
                    "ResourceAlerter Viewer", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else if (process.ExitCode == 2)
            {
                MessageBox.Show(Strings.Viewer_SummaryNoChannel,
                    "ResourceAlerter Viewer", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show(
                    Strings.Viewer_SummarySendFailed,
                    "ResourceAlerter Viewer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // User declined the UAC elevation prompt — not worth alarming over.
        }
        catch (Exception ex)
        {
            MessageBox.Show(Strings.Viewer_SummarySendError(ex.Message),
                "ResourceAlerter Viewer", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _sendSummaryButton.Enabled = true;
            _sendSummaryButton.Text = Strings.Viewer_SendTodaySummary;
        }
    }

    private void OpenSettings()
    {
        var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        using var form = new SettingsForm(configPath);
        form.ShowDialog(this);
        Refresh(fullReload: true);
    }

    private void Refresh(bool fullReload)
    {
        try
        {
            if (!_reader.DatabaseExists())
            {
                _currentValueLabel.Text = Strings.Viewer_DatabaseNotFound;
                return;
            }

            if (fullReload || _seriesCombo.Items.Count == 0)
            {
                var selected = _seriesCombo.SelectedItem as SeriesKey;
                _seriesCombo.Items.Clear();
                foreach (var series in _reader.GetSeries())
                {
                    _seriesCombo.Items.Add(series);
                }

                if (_seriesCombo.Items.Count > 0)
                {
                    var restoreIndex = 0;
                    if (selected is not null)
                    {
                        for (var i = 0; i < _seriesCombo.Items.Count; i++)
                        {
                            if (_seriesCombo.Items[i] is SeriesKey key && key.Monitor == selected.Monitor && key.Subject == selected.Subject)
                            {
                                restoreIndex = i;
                                break;
                            }
                        }
                    }
                    _seriesCombo.SelectedIndex = restoreIndex; // triggers LoadSelectedSeries
                    return;
                }
            }

            // fullReload (explicit "Refrescar" click, or the very first load) resets the zoom;
            // the 30s auto-refresh timer calls this with fullReload=false specifically so it
            // DOESN'T reset the zoom — otherwise a chart the user zoomed into to inspect an
            // alert would snap back to the full 24h view on its own every 30 seconds.
            LoadSelectedSeries(resetZoom: fullReload);
        }
        catch (Exception ex)
        {
            _currentValueLabel.Text = Strings.Viewer_Error(ex.Message);
        }
    }

    /// <summary>
    /// ScottPlot's FormsPlot already supports mouse zoom/pan out of the box (scroll wheel,
    /// drag, right-click for a menu with "Auto Axis") — no custom input handling needed here.
    /// <paramref name="resetZoom"/> controls whether this call resets the view back to fit-all.
    /// <see cref="ScottPlot.Plot.Clear"/> itself does not touch axis limits, so a plain
    /// Clear()+re-add of the data would already preserve zoom — the part that actually needed
    /// gating was <c>Axes.DateTimeTicksBottom()</c>, which replaces the bottom axis object
    /// (resetting its limits) and so must only run when actually resetting the view.
    /// </summary>
    private void LoadSelectedSeries(bool resetZoom = true)
    {
        if (_seriesCombo.SelectedItem is not SeriesKey series)
        {
            return;
        }

        var range = (_rangeCombo.SelectedItem as TimeRangeOption)?.Range ?? TimeSpan.FromHours(24);

        try
        {
            var (latest, samples) = _reader.GetSamples(series, range);
            var alerts = _reader.GetAlerts(series, range);

            _currentValueLabel.Text = latest is null
                ? Strings.Viewer_NoDataRecorded
                : $"{Strings.FormatNumber(latest.Value, "F2")} {series.Unit} — {latest.Timestamp.LocalDateTime:yyyy-MM-dd HH:mm:ss}";

            var plot = _plot.Plot;
            plot.Clear();

            if (samples.Count > 0)
            {
                var xs = samples.Select(s => s.Timestamp.LocalDateTime.ToOADate()).ToArray();
                var ys = samples.Select(s => s.Value).ToArray();
                _loadedXs = xs;
                _loadedYs = ys;

                var scatter = plot.Add.Scatter(xs, ys);
                scatter.MarkerSize = 0;
                scatter.LineWidth = 1.5f;
                scatter.Color = new ScottPlot.Color(31, 119, 180);
                scatter.FillY = true;
                scatter.FillYColor = scatter.Color.WithAlpha(0.15);
                scatter.FillYValue = ys.Min();
            }
            else
            {
                _loadedXs = Array.Empty<double>();
                _loadedYs = Array.Empty<double>();
            }

            foreach (var alert in alerts)
            {
                var line = plot.Add.VerticalLine(alert.StartedAt.LocalDateTime.ToOADate());
                line.Color = ScottPlot.Colors.Red;
                line.LineWidth = 1.5f;
            }

            plot.Title(Strings.Viewer_LastNHours(series.ToString(), range.TotalHours));
            if (!string.IsNullOrEmpty(series.Unit))
            {
                plot.YLabel(series.Unit);
            }
            if (resetZoom)
            {
                // DateTimeTicksBottom() replaces the bottom axis object, which resets its
                // limits — that's the actual reason a periodic refresh was wiping out the
                // user's zoom (Clear() + re-adding data alone does NOT touch axis limits, only
                // this call does). Only calling it when resetZoom is true is what lets a
                // periodic data refresh leave the user's current zoom/pan exactly where they
                // left it; Clear() doesn't undo the tick formatting this already set up.
                plot.Axes.DateTimeTicksBottom();
                plot.Axes.AutoScale();

                // Seed the "last seen" X range to the freshly auto-scaled one, so the Y-rescale
                // timer's next tick sees no change and doesn't immediately redo the Y fit with
                // its own (slightly different) padding right on top of what AutoScale just did.
                var freshLimits = plot.Axes.GetLimits();
                _lastSeenXLeft = freshLimits.Left;
                _lastSeenXRight = freshLimits.Right;
            }

            _plot.Refresh();
        }
        catch (Exception ex)
        {
            _currentValueLabel.Text = Strings.Viewer_Error(ex.Message);
        }
    }

    private sealed record TimeRangeOption(string Label, TimeSpan Range)
    {
        public override string ToString() => Label;
    }
}
