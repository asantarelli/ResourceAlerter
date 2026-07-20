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
    private readonly Label _currentValueLabel;
    private readonly Label _autoRefreshLabel;
    private readonly Button _refreshButton;
    private readonly Button _sendSummaryButton;
    private readonly Button _settingsButton;
    private readonly FormsPlot _plot;
    private readonly System.Windows.Forms.Timer _autoRefreshTimer;

    public MainForm(DataReader reader)
    {
        _reader = reader;

        Text = Strings.Viewer_Title(Environment.MachineName);
        Width = 1000;
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

        _refreshButton = new Button { Text = Strings.Viewer_Refresh, Left = 340, Top = 8, Width = 90 };
        _refreshButton.Click += (_, _) => Refresh(fullReload: true); // also picks up newly-recorded series

        _sendSummaryButton = new Button { Text = Strings.Viewer_SendTodaySummary, Left = 440, Top = 8, Width = 160 };
        _sendSummaryButton.Click += async (_, _) => await SendTodaySummaryAsync();

        _currentValueLabel = new Label
        {
            Left = 610,
            Top = 12,
            AutoSize = true,
            Font = new Font(Font.FontFamily, 10, FontStyle.Bold),
            Text = "—",
        };

        _autoRefreshLabel = new Label
        {
            Left = 610,
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
        topPanel.Controls.Add(_refreshButton);
        topPanel.Controls.Add(_sendSummaryButton);
        topPanel.Controls.Add(_currentValueLabel);
        topPanel.Controls.Add(_autoRefreshLabel);
        topPanel.Controls.Add(_settingsButton);

        _plot = new FormsPlot { Dock = DockStyle.Fill };

        Controls.Add(_plot);
        Controls.Add(topPanel);

        // Keeps the chart current even if nobody touches the window — the whole point of a
        // monitoring viewer is that it stays accurate while just sitting open on a screen.
        _autoRefreshTimer = new System.Windows.Forms.Timer { Interval = (int)AutoRefreshInterval.TotalMilliseconds };
        _autoRefreshTimer.Tick += (_, _) => Refresh(fullReload: false);
        FormClosed += (_, _) => _autoRefreshTimer.Stop();

        Load += (_, _) =>
        {
            Refresh(fullReload: true);
            _autoRefreshTimer.Start();
        };
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

            LoadSelectedSeries();
        }
        catch (Exception ex)
        {
            _currentValueLabel.Text = Strings.Viewer_Error(ex.Message);
        }
    }

    private void LoadSelectedSeries()
    {
        if (_seriesCombo.SelectedItem is not SeriesKey series)
        {
            return;
        }

        try
        {
            var (latest, samples) = _reader.GetSamples(series);
            var alerts = _reader.GetAlerts24h(series);

            _currentValueLabel.Text = latest is null
                ? Strings.Viewer_NoDataRecorded
                : $"{Strings.FormatNumber(latest.Value, "F2")} {series.Unit} — {latest.Timestamp.LocalDateTime:yyyy-MM-dd HH:mm:ss}";

            var plot = _plot.Plot;
            plot.Clear();

            if (samples.Count > 0)
            {
                var xs = samples.Select(s => s.Timestamp.LocalDateTime.ToOADate()).ToArray();
                var ys = samples.Select(s => s.Value).ToArray();

                var scatter = plot.Add.Scatter(xs, ys);
                scatter.MarkerSize = 0;
                scatter.LineWidth = 1.5f;
                scatter.Color = new ScottPlot.Color(31, 119, 180);
                scatter.FillY = true;
                scatter.FillYColor = scatter.Color.WithAlpha(0.15);
                scatter.FillYValue = ys.Min();
            }

            foreach (var alert in alerts)
            {
                var line = plot.Add.VerticalLine(alert.StartedAt.LocalDateTime.ToOADate());
                line.Color = ScottPlot.Colors.Red;
                line.LineWidth = 1.5f;
            }

            plot.Axes.DateTimeTicksBottom();
            plot.Title(Strings.Viewer_Last24Hours(series.ToString()));
            if (!string.IsNullOrEmpty(series.Unit))
            {
                plot.YLabel(series.Unit);
            }
            plot.Axes.AutoScale();

            _plot.Refresh();
        }
        catch (Exception ex)
        {
            _currentValueLabel.Text = Strings.Viewer_Error(ex.Message);
        }
    }
}
