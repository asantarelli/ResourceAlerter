namespace ResourceAlerter.Viewer;

/// <summary>Small helpers to keep SettingsForm's tab-building code from being pure boilerplate.</summary>
internal static class FieldFactory
{
    public static TableLayoutPanel NewPanel(int rows)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = rows,
            AutoScroll = true,
            Padding = new Padding(12),
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < rows; i++)
        {
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }
        return panel;
    }

    private static Label NewLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(3, 8, 10, 3),
    };

    public static TextBox AddText(TableLayoutPanel panel, int row, string label, string value, bool password = false)
    {
        panel.Controls.Add(NewLabel(label), 0, row);
        var tb = new TextBox
        {
            Text = value,
            Width = 300,
            Anchor = AnchorStyles.Left,
            UseSystemPasswordChar = password,
        };
        panel.Controls.Add(tb, 1, row);
        return tb;
    }

    public static TextBox AddMultilineText(TableLayoutPanel panel, int row, string label, string value, int height = 70)
    {
        panel.Controls.Add(NewLabel(label), 0, row);
        var tb = new TextBox
        {
            Text = value,
            Width = 300,
            Height = height,
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            Anchor = AnchorStyles.Left,
        };
        panel.Controls.Add(tb, 1, row);
        return tb;
    }

    public static NumericUpDown AddNumeric(TableLayoutPanel panel, int row, string label, decimal value, decimal min = 0, decimal max = 1_000_000, int decimals = 0)
    {
        panel.Controls.Add(NewLabel(label), 0, row);
        var nud = new NumericUpDown
        {
            Minimum = min,
            Maximum = max,
            Value = Math.Clamp(value, min, max),
            DecimalPlaces = decimals,
            Width = 100,
            Anchor = AnchorStyles.Left,
        };
        panel.Controls.Add(nud, 1, row);
        return nud;
    }

    public static CheckBox AddCheck(TableLayoutPanel panel, int row, string label, bool value)
    {
        panel.Controls.Add(NewLabel(label), 0, row);
        var cb = new CheckBox { Checked = value, Anchor = AnchorStyles.Left };
        panel.Controls.Add(cb, 1, row);
        return cb;
    }

    public static Label AddSectionHeader(TableLayoutPanel panel, int row, string text)
    {
        var header = new Label
        {
            Text = text,
            AutoSize = true,
            Font = new Font(SystemFonts.DefaultFont.FontFamily, 9.5f, FontStyle.Bold),
            Margin = new Padding(3, 12, 3, 6),
        };
        panel.Controls.Add(header, 0, row);
        panel.SetColumnSpan(header, 2);
        return header;
    }

    public static string JoinLines(IEnumerable<string> values) => string.Join(Environment.NewLine, values);

    public static List<string> SplitLines(string text) =>
        text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    public static string JoinCsv(IEnumerable<string> values) => string.Join(", ", values);

    public static List<string> SplitCsv(string text) =>
        text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>"Key=Value" per line, for the small rail/override dictionaries.</summary>
    public static string JoinKeyValue(IEnumerable<KeyValuePair<string, string>> pairs) =>
        string.Join(Environment.NewLine, pairs.Select(p => $"{p.Key}={p.Value}"));

    public static Dictionary<string, string> SplitKeyValue(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in SplitLines(text))
        {
            var idx = line.IndexOf('=');
            if (idx <= 0)
            {
                continue;
            }
            var key = line[..idx].Trim();
            var value = line[(idx + 1)..].Trim();
            if (key.Length > 0)
            {
                result[key] = value;
            }
        }
        return result;
    }
}
