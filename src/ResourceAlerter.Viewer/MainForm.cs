using ScottPlot.WinForms;

namespace ResourceAlerter.Viewer;

/// <summary>
/// Single-window viewer over the service's recorded data: pick a variable, see its
/// last/current value, and a 24-hour area chart with red vertical lines at alert starts.
/// </summary>
public sealed class MainForm : Form
{
    private readonly DataReader _reader;
    private readonly ComboBox _seriesCombo;
    private readonly Label _currentValueLabel;
    private readonly Button _refreshButton;
    private readonly FormsPlot _plot;

    public MainForm(DataReader reader)
    {
        _reader = reader;

        Text = $"ResourceAlerter Viewer — {Environment.MachineName}";
        Width = 1000;
        Height = 600;
        StartPosition = FormStartPosition.CenterScreen;

        var topPanel = new Panel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(8) };

        _seriesCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 320,
            Left = 8,
            Top = 9,
        };
        _seriesCombo.SelectedIndexChanged += (_, _) => LoadSelectedSeries();

        _refreshButton = new Button { Text = "Refrescar", Left = 340, Top = 8, Width = 90 };
        _refreshButton.Click += (_, _) => Refresh(fullReload: false);

        _currentValueLabel = new Label
        {
            Left = 445,
            Top = 12,
            AutoSize = true,
            Font = new Font(Font.FontFamily, 10, FontStyle.Bold),
            Text = "—",
        };

        topPanel.Controls.Add(_seriesCombo);
        topPanel.Controls.Add(_refreshButton);
        topPanel.Controls.Add(_currentValueLabel);

        _plot = new FormsPlot { Dock = DockStyle.Fill };

        Controls.Add(_plot);
        Controls.Add(topPanel);

        Load += (_, _) => Refresh(fullReload: true);
    }

    private void Refresh(bool fullReload)
    {
        try
        {
            if (!_reader.DatabaseExists())
            {
                _currentValueLabel.Text = "Base de datos no encontrada — ¿está corriendo el servicio?";
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
            _currentValueLabel.Text = $"Error: {ex.Message}";
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
                ? "Sin datos registrados"
                : $"{latest.Value:F2} {series.Unit} — {latest.Timestamp.LocalDateTime:yyyy-MM-dd HH:mm:ss}";

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
            plot.Title($"{series} — últimas 24 horas");
            if (!string.IsNullOrEmpty(series.Unit))
            {
                plot.YLabel(series.Unit);
            }
            plot.Axes.AutoScale();

            _plot.Refresh();
        }
        catch (Exception ex)
        {
            _currentValueLabel.Text = $"Error: {ex.Message}";
        }
    }
}
