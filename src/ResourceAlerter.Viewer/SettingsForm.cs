using ResourceAlerter.Configuration;
using static ResourceAlerter.Viewer.FieldFactory;

namespace ResourceAlerter.Viewer;

/// <summary>
/// Full configuration editor: loads the existing appsettings.json (or C# defaults if none
/// exists yet), lets the admin edit every setting via form fields instead of hand-editing JSON,
/// and writes it back out in the exact shape the service expects. Saving offers to restart the
/// service (elevated) so changes take effect immediately.
/// </summary>
public sealed class SettingsForm : Form
{
    private readonly string _configPath;
    private ConfigBundle _bundle;

    // General
    private TextBox _machineNameBox = null!;
    private NumericUpDown _pollingIntervalBox = null!;

    // SMTP
    private TextBox _smtpHostBox = null!;
    private NumericUpDown _smtpPortBox = null!;
    private CheckBox _smtpUseSslBox = null!;
    private CheckBox _smtpRequiresAuthBox = null!;
    private TextBox _smtpUsernameBox = null!;
    private TextBox _smtpPasswordBox = null!;
    private TextBox _smtpFromAddressBox = null!;
    private TextBox _smtpFromDisplayNameBox = null!;
    private TextBox _smtpRecipientsBox = null!;
    private NumericUpDown _smtpRetryCountBox = null!;
    private NumericUpDown _smtpRetryBackoffBox = null!;
    private NumericUpDown _smtpTimeoutBox = null!;

    // Discord
    private CheckBox _discordEnabledBox = null!;
    private TextBox _discordWebhookBox = null!;

    // Per-monitor controls, keyed by monitor name
    private readonly Dictionary<string, MonitorControls> _monitorControls = new();

    // Voltage-specific
    private TextBox _voltageRailsBox = null!;
    private TextBox _voltageOverridesBox = null!;

    // Network-specific
    private TextBox _networkTargetHostBox = null!;
    private TextBox _networkFallbackHostBox = null!;
    private NumericUpDown _networkPingIntervalBox = null!;
    private NumericUpDown _networkPingTimeoutBox = null!;
    private NumericUpDown _networkWindowSizeBox = null!;
    private NumericUpDown _networkMaxLossesBox = null!;
    private NumericUpDown _networkMaxOutageBox = null!;

    // Disk-specific
    private NumericUpDown _diskPercentThresholdBox = null!;
    private NumericUpDown _diskAbsoluteGbBox = null!;
    private TextBox _diskDrivesBox = null!;

    // Temperature-specific
    private NumericUpDown _temperatureThresholdBox = null!;

    // Database
    private TextBox _dbPathBox = null!;
    private NumericUpDown _dbRetentionBox = null!;

    // FileLogging
    private TextBox _logDirectoryBox = null!;
    private NumericUpDown _logMaxSizeBox = null!;
    private NumericUpDown _logRetentionBox = null!;

    private sealed class MonitorControls
    {
        public CheckBox Enabled = null!;
        public NumericUpDown SustainedWindowSeconds = null!;
        public NumericUpDown RecoveryWindowSeconds = null!;
        public NumericUpDown ReminderIntervalMinutes = null!;
    }

    public SettingsForm(string configPath)
    {
        _configPath = configPath;
        _bundle = ConfigStore.Load(configPath);

        Text = "ResourceAlerter — Configuración";
        Width = 620;
        Height = 640;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;

        try
        {
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        }
        catch
        {
            // Cosmetic only.
        }

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildGeneralTab());
        tabs.TabPages.Add(BuildSmtpTab());
        tabs.TabPages.Add(BuildDiscordTab());
        tabs.TabPages.Add(BuildMonitorTab("CPU", _bundle.Monitoring.Cpu, extra: null));
        tabs.TabPages.Add(BuildMonitorTab("Memoria", _bundle.Monitoring.Memory, extra: null));
        tabs.TabPages.Add(BuildDiskTab());
        tabs.TabPages.Add(BuildTemperatureTab());
        tabs.TabPages.Add(BuildVoltageTab());
        tabs.TabPages.Add(BuildNetworkTab());
        tabs.TabPages.Add(BuildDatabaseTab());
        tabs.TabPages.Add(BuildLoggingTab());

        var buttonPanel = new Panel { Dock = DockStyle.Bottom, Height = 48 };
        var saveButton = new Button { Text = "Guardar y reiniciar servicio", Width = 200, Height = 32, Left = 12, Top = 8 };
        var cancelButton = new Button { Text = "Cancelar", Width = 90, Height = 32, Left = 220, Top = 8 };
        saveButton.Click += async (_, _) => await SaveAsync();
        cancelButton.Click += (_, _) => Close();
        buttonPanel.Controls.Add(saveButton);
        buttonPanel.Controls.Add(cancelButton);

        Controls.Add(tabs);
        Controls.Add(buttonPanel);
    }

    private TabPage BuildGeneralTab()
    {
        var page = new TabPage("General");
        var panel = NewPanel(3);
        _machineNameBox = AddText(panel, 0, "Nombre de máquina (mails/logs):", _bundle.General.MachineName ?? "");
        _pollingIntervalBox = AddNumeric(panel, 1, "Intervalo de sondeo (segundos):", _bundle.Monitoring.PollingIntervalSeconds, 1, 3600);
        var note = new Label
        {
            Text = "Dejar el nombre de máquina vacío usa el nombre real de Windows.\nEsto no afecta el archivo appsettings.<MAQUINA>.json, que siempre usa el nombre real.",
            AutoSize = true,
            ForeColor = Color.Gray,
        };
        panel.Controls.Add(note, 0, 2);
        panel.SetColumnSpan(note, 2);
        page.Controls.Add(panel);
        return page;
    }

    private TabPage BuildSmtpTab()
    {
        var page = new TabPage("SMTP");
        var panel = NewPanel(12);
        _smtpHostBox = AddText(panel, 0, "Servidor (Host):", _bundle.Smtp.Host);
        _smtpPortBox = AddNumeric(panel, 1, "Puerto:", _bundle.Smtp.Port, 1, 65535);
        _smtpUseSslBox = AddCheck(panel, 2, "Usar SSL/TLS:", _bundle.Smtp.UseSsl);
        _smtpRequiresAuthBox = AddCheck(panel, 3, "Requiere autenticación:", _bundle.Smtp.RequiresAuthentication);
        _smtpUsernameBox = AddText(panel, 4, "Usuario:", _bundle.Smtp.Username);
        _smtpPasswordBox = AddText(panel, 5, "Contraseña:", _bundle.Smtp.Password, password: true);
        _smtpFromAddressBox = AddText(panel, 6, "Dirección remitente:", _bundle.Smtp.FromAddress);
        _smtpFromDisplayNameBox = AddText(panel, 7, "Nombre remitente:", _bundle.Smtp.FromDisplayName);
        _smtpRecipientsBox = AddMultilineText(panel, 8, "Destinatarios (uno por línea):", JoinLines(_bundle.Smtp.Recipients));
        _smtpRetryCountBox = AddNumeric(panel, 9, "Reintentos de envío:", _bundle.Smtp.RetryCount, 1, 10);
        _smtpRetryBackoffBox = AddNumeric(panel, 10, "Espera entre reintentos (segundos):", _bundle.Smtp.RetryBackoffSeconds, 1, 300);
        _smtpTimeoutBox = AddNumeric(panel, 11, "Timeout (milisegundos):", _bundle.Smtp.TimeoutMilliseconds, 1000, 300_000);
        page.Controls.Add(panel);
        return page;
    }

    private TabPage BuildDiscordTab()
    {
        var page = new TabPage("Discord");
        var panel = NewPanel(3);
        _discordEnabledBox = AddCheck(panel, 0, "Habilitado:", _bundle.Discord.Enabled);
        _discordWebhookBox = AddText(panel, 1, "URL del webhook:", _bundle.Discord.WebhookUrl);
        var note = new Label
        {
            Text = "Configuración del canal de Discord → Integraciones → Webhooks. No hace falta bot.\nSe manda en paralelo al mail, sin adjuntos (gráficos/logs quedan solo en el mail).",
            AutoSize = true,
            ForeColor = Color.Gray,
        };
        panel.Controls.Add(note, 0, 2);
        panel.SetColumnSpan(note, 2);
        page.Controls.Add(panel);
        return page;
    }

    private TabPage BuildMonitorTab(string title, MonitorOptionsBase options, Action<TableLayoutPanel, int>? extra)
    {
        var page = new TabPage(title);
        var panel = NewPanel(6);
        var row = 0;

        var controls = new MonitorControls { Enabled = AddCheck(panel, row++, "Habilitado:", options.Enabled) };

        row = AddMonitorSpecificFields(panel, row, title, options);

        controls.SustainedWindowSeconds = AddNumeric(panel, row++, "Ventana sostenida (segundos):", options.SustainedWindowSeconds, 1, 3600);
        controls.RecoveryWindowSeconds = AddNumeric(panel, row++, "Ventana de recuperación (segundos):", options.RecoveryWindowSeconds, 1, 3600);
        controls.ReminderIntervalMinutes = AddNumeric(panel, row, "Recordatorio cada (minutos):", options.ReminderIntervalMinutes, 1, 1440);

        _monitorControls[title] = controls;
        page.Controls.Add(panel);
        return page;
    }

    /// <summary>CPU/Memory share the alert+recovery threshold shape; other monitors have their own tabs/fields.</summary>
    private int AddMonitorSpecificFields(TableLayoutPanel panel, int row, string title, MonitorOptionsBase options)
    {
        if (options is CpuOptions cpu)
        {
            var alert = AddNumeric(panel, row++, "Umbral de alerta (%):", (decimal)cpu.AlertThresholdPercent, 0, 100, 1);
            var recovery = AddNumeric(panel, row++, "Umbral de recuperación (%):", (decimal)cpu.RecoveryThresholdPercent, 0, 100, 1);
            _cpuAlert = alert;
            _cpuRecovery = recovery;
        }
        else if (options is MemoryOptions mem)
        {
            var alert = AddNumeric(panel, row++, "Umbral de alerta (%):", (decimal)mem.AlertThresholdPercent, 0, 100, 1);
            var recovery = AddNumeric(panel, row++, "Umbral de recuperación (%):", (decimal)mem.RecoveryThresholdPercent, 0, 100, 1);
            _memAlert = alert;
            _memRecovery = recovery;
        }

        return row;
    }

    private NumericUpDown _cpuAlert = null!, _cpuRecovery = null!, _memAlert = null!, _memRecovery = null!;

    private TabPage BuildDiskTab()
    {
        var page = new TabPage("Disco");
        var panel = NewPanel(8);
        var disk = _bundle.Monitoring.Disk;

        var controls = new MonitorControls { Enabled = AddCheck(panel, 0, "Habilitado:", disk.Enabled) };
        _diskPercentThresholdBox = AddNumeric(panel, 1, "Umbral de espacio libre (%):", (decimal)disk.FreeSpacePercentThreshold, 0, 100, 1);
        _diskAbsoluteGbBox = AddNumeric(panel, 2, "Umbral de espacio libre (GB):", (decimal)disk.FreeSpaceAbsoluteGbThreshold, 0, 10_000, 1);
        _diskDrivesBox = AddText(panel, 3, "Unidades a vigilar (ej: D: o C:, D:):", JoinCsv(disk.Drives));
        controls.SustainedWindowSeconds = AddNumeric(panel, 4, "Ventana sostenida (segundos):", disk.SustainedWindowSeconds, 1, 3600);
        controls.RecoveryWindowSeconds = AddNumeric(panel, 5, "Ventana de recuperación (segundos):", disk.RecoveryWindowSeconds, 1, 3600);
        controls.ReminderIntervalMinutes = AddNumeric(panel, 6, "Recordatorio cada (minutos):", disk.ReminderIntervalMinutes, 1, 1440);

        var note = new Label
        {
            Text = "Vacío = solo la unidad de sistema (normalmente C:). Si ponés una o más unidades acá,\n" +
                   "REEMPLAZAN a la de sistema — no se suman. Por ej: poné solo \"D:\" para vigilar la unidad\n" +
                   "de temporales/swap en vez de C:, o \"C:, D:\" para vigilar ambas.",
            AutoSize = true,
            ForeColor = Color.Gray,
        };
        panel.Controls.Add(note, 0, 7);
        panel.SetColumnSpan(note, 2);

        _monitorControls["Disco"] = controls;
        page.Controls.Add(panel);
        return page;
    }

    private TabPage BuildTemperatureTab()
    {
        var page = new TabPage("Temperatura");
        var panel = NewPanel(6);
        var temp = _bundle.Monitoring.Temperature;

        var controls = new MonitorControls { Enabled = AddCheck(panel, 0, "Habilitado:", temp.Enabled) };
        _temperatureThresholdBox = AddNumeric(panel, 1, "Umbral de alerta (°C):", (decimal)temp.AlertThresholdCelsius, 0, 150, 1);
        controls.SustainedWindowSeconds = AddNumeric(panel, 2, "Ventana sostenida (segundos):", temp.SustainedWindowSeconds, 1, 3600);
        controls.RecoveryWindowSeconds = AddNumeric(panel, 3, "Ventana de recuperación (segundos):", temp.RecoveryWindowSeconds, 1, 3600);
        controls.ReminderIntervalMinutes = AddNumeric(panel, 4, "Recordatorio cada (minutos):", temp.ReminderIntervalMinutes, 1, 1440);

        _monitorControls["Temperatura"] = controls;
        page.Controls.Add(panel);
        return page;
    }

    private TabPage BuildVoltageTab()
    {
        var page = new TabPage("Voltaje");
        var panel = NewPanel(8);
        var volt = _bundle.Monitoring.Voltage;

        var controls = new MonitorControls { Enabled = AddCheck(panel, 0, "Habilitado:", volt.Enabled) };
        var deviationPercent = (decimal)(volt.AllowedDeviationFraction * 100);
        _voltageDeviationBox = AddNumeric(panel, 1, "Desviación permitida (%):", deviationPercent, 0, 100, 1);
        _voltageRailsBox = AddMultilineText(panel, 2, "Rieles nominales (Nombre=Voltios):",
            JoinKeyValue(volt.NominalRails.Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value.ToString("0.##")))));
        _voltageOverridesBox = AddMultilineText(panel, 3, "Nombres de sensor reales (Riel=Sensor, opcional):",
            JoinKeyValue(volt.SensorNameOverrides), height: 50);
        controls.SustainedWindowSeconds = AddNumeric(panel, 4, "Ventana sostenida (segundos):", volt.SustainedWindowSeconds, 1, 3600);
        controls.RecoveryWindowSeconds = AddNumeric(panel, 5, "Ventana de recuperación (segundos):", volt.RecoveryWindowSeconds, 1, 3600);
        controls.ReminderIntervalMinutes = AddNumeric(panel, 6, "Recordatorio cada (minutos):", volt.ReminderIntervalMinutes, 1, 1440);

        var note = new Label
        {
            Text = "Usá \"ResourceAlerter.exe --list-sensors\" (elevado) para ver los nombres reales de sensores.",
            AutoSize = true,
            ForeColor = Color.Gray,
        };
        panel.Controls.Add(note, 0, 7);
        panel.SetColumnSpan(note, 2);

        _monitorControls["Voltaje"] = controls;
        page.Controls.Add(panel);
        return page;
    }

    private NumericUpDown _voltageDeviationBox = null!;

    private TabPage BuildNetworkTab()
    {
        var page = new TabPage("Red");
        var panel = NewPanel(11);
        var net = _bundle.Monitoring.Network;

        var controls = new MonitorControls { Enabled = AddCheck(panel, 0, "Habilitado:", net.Enabled) };
        _networkTargetHostBox = AddText(panel, 1, "Host a pingear (vacío = auto-detectar gateway):", net.TargetHost ?? "");
        _networkFallbackHostBox = AddText(panel, 2, "Host de respaldo:", net.FallbackHost);
        _networkPingIntervalBox = AddNumeric(panel, 3, "Intervalo de ping (segundos):", net.PingIntervalSeconds, 1, 3600);
        _networkPingTimeoutBox = AddNumeric(panel, 4, "Timeout de ping (milisegundos):", net.PingTimeoutMilliseconds, 100, 60_000);
        _networkWindowSizeBox = AddNumeric(panel, 5, "Tamaño de ventana (cantidad de pings):", net.WindowSize, 1, 100);
        _networkMaxLossesBox = AddNumeric(panel, 6, "Máx. pérdidas en la ventana:", net.MaxLossesInWindow, 0, 100);
        _networkMaxOutageBox = AddNumeric(panel, 7, "Máx. corte continuo (segundos):", net.MaxConsecutiveOutageSeconds, 1, 3600);
        controls.SustainedWindowSeconds = AddNumeric(panel, 8, "Ventana sostenida (segundos):", net.SustainedWindowSeconds, 1, 3600);
        controls.RecoveryWindowSeconds = AddNumeric(panel, 9, "Ventana de recuperación (segundos):", net.RecoveryWindowSeconds, 1, 3600);
        _networkReminderBox = AddNumeric(panel, 10, "Recordatorio cada (minutos):", net.ReminderIntervalMinutes, 1, 1440);

        _monitorControls["Red"] = controls;
        page.Controls.Add(panel);
        return page;
    }

    private NumericUpDown _networkReminderBox = null!;

    private TabPage BuildDatabaseTab()
    {
        var page = new TabPage("Base de datos");
        var panel = NewPanel(2);
        _dbPathBox = AddText(panel, 0, "Ruta del archivo SQLite:", _bundle.Database.Path);
        _dbRetentionBox = AddNumeric(panel, 1, "Retención de datos (días):", _bundle.Database.RetentionDays, 1, 3650);
        page.Controls.Add(panel);
        return page;
    }

    private TabPage BuildLoggingTab()
    {
        var page = new TabPage("Logs");
        var panel = NewPanel(3);
        _logDirectoryBox = AddText(panel, 0, "Carpeta de logs:", _bundle.FileLogging.Directory);
        _logMaxSizeBox = AddNumeric(panel, 1, "Tamaño máximo por archivo (MB):", _bundle.FileLogging.MaxFileSizeMb, 1, 1000);
        _logRetentionBox = AddNumeric(panel, 2, "Retención de logs (días):", _bundle.FileLogging.RetentionDays, 1, 3650);
        page.Controls.Add(panel);
        return page;
    }

    private void ApplyControlsToBundle()
    {
        _bundle.General.MachineName = string.IsNullOrWhiteSpace(_machineNameBox.Text) ? null : _machineNameBox.Text.Trim();
        _bundle.Monitoring.PollingIntervalSeconds = (int)_pollingIntervalBox.Value;

        _bundle.Smtp.Host = _smtpHostBox.Text.Trim();
        _bundle.Smtp.Port = (int)_smtpPortBox.Value;
        _bundle.Smtp.UseSsl = _smtpUseSslBox.Checked;
        _bundle.Smtp.RequiresAuthentication = _smtpRequiresAuthBox.Checked;
        _bundle.Smtp.Username = _smtpUsernameBox.Text.Trim();
        _bundle.Smtp.Password = _smtpPasswordBox.Text;
        _bundle.Smtp.FromAddress = _smtpFromAddressBox.Text.Trim();
        _bundle.Smtp.FromDisplayName = _smtpFromDisplayNameBox.Text.Trim();
        _bundle.Smtp.Recipients = SplitLines(_smtpRecipientsBox.Text);
        _bundle.Smtp.RetryCount = (int)_smtpRetryCountBox.Value;
        _bundle.Smtp.RetryBackoffSeconds = (int)_smtpRetryBackoffBox.Value;
        _bundle.Smtp.TimeoutMilliseconds = (int)_smtpTimeoutBox.Value;

        _bundle.Discord.Enabled = _discordEnabledBox.Checked;
        _bundle.Discord.WebhookUrl = _discordWebhookBox.Text.Trim();

        ApplyMonitorBase(_bundle.Monitoring.Cpu, "CPU");
        _bundle.Monitoring.Cpu.AlertThresholdPercent = (double)_cpuAlert.Value;
        _bundle.Monitoring.Cpu.RecoveryThresholdPercent = (double)_cpuRecovery.Value;

        ApplyMonitorBase(_bundle.Monitoring.Memory, "Memoria");
        _bundle.Monitoring.Memory.AlertThresholdPercent = (double)_memAlert.Value;
        _bundle.Monitoring.Memory.RecoveryThresholdPercent = (double)_memRecovery.Value;

        ApplyMonitorBase(_bundle.Monitoring.Disk, "Disco");
        _bundle.Monitoring.Disk.FreeSpacePercentThreshold = (double)_diskPercentThresholdBox.Value;
        _bundle.Monitoring.Disk.FreeSpaceAbsoluteGbThreshold = (double)_diskAbsoluteGbBox.Value;
        _bundle.Monitoring.Disk.Drives = SplitCsv(_diskDrivesBox.Text);

        ApplyMonitorBase(_bundle.Monitoring.Temperature, "Temperatura");
        _bundle.Monitoring.Temperature.AlertThresholdCelsius = (double)_temperatureThresholdBox.Value;

        ApplyMonitorBase(_bundle.Monitoring.Voltage, "Voltaje");
        _bundle.Monitoring.Voltage.AllowedDeviationFraction = (double)(_voltageDeviationBox.Value / 100m);
        _bundle.Monitoring.Voltage.NominalRails = SplitKeyValue(_voltageRailsBox.Text)
            .ToDictionary(kv => kv.Key, kv => double.TryParse(kv.Value, out var v) ? v : 0.0);
        _bundle.Monitoring.Voltage.SensorNameOverrides = SplitKeyValue(_voltageOverridesBox.Text);

        var netControls = _monitorControls["Red"];
        _bundle.Monitoring.Network.Enabled = netControls.Enabled.Checked;
        _bundle.Monitoring.Network.SustainedWindowSeconds = (int)netControls.SustainedWindowSeconds.Value;
        _bundle.Monitoring.Network.RecoveryWindowSeconds = (int)netControls.RecoveryWindowSeconds.Value;
        _bundle.Monitoring.Network.ReminderIntervalMinutes = (int)_networkReminderBox.Value;
        _bundle.Monitoring.Network.TargetHost = string.IsNullOrWhiteSpace(_networkTargetHostBox.Text) ? null : _networkTargetHostBox.Text.Trim();
        _bundle.Monitoring.Network.FallbackHost = _networkFallbackHostBox.Text.Trim();
        _bundle.Monitoring.Network.PingIntervalSeconds = (int)_networkPingIntervalBox.Value;
        _bundle.Monitoring.Network.PingTimeoutMilliseconds = (int)_networkPingTimeoutBox.Value;
        _bundle.Monitoring.Network.WindowSize = (int)_networkWindowSizeBox.Value;
        _bundle.Monitoring.Network.MaxLossesInWindow = (int)_networkMaxLossesBox.Value;
        _bundle.Monitoring.Network.MaxConsecutiveOutageSeconds = (int)_networkMaxOutageBox.Value;

        _bundle.Database.Path = _dbPathBox.Text.Trim();
        _bundle.Database.RetentionDays = (int)_dbRetentionBox.Value;

        _bundle.FileLogging.Directory = _logDirectoryBox.Text.Trim();
        _bundle.FileLogging.MaxFileSizeMb = (int)_logMaxSizeBox.Value;
        _bundle.FileLogging.RetentionDays = (int)_logRetentionBox.Value;
    }

    private void ApplyMonitorBase(MonitorOptionsBase options, string key)
    {
        var controls = _monitorControls[key];
        options.Enabled = controls.Enabled.Checked;
        options.SustainedWindowSeconds = (int)controls.SustainedWindowSeconds.Value;
        options.RecoveryWindowSeconds = (int)controls.RecoveryWindowSeconds.Value;
        options.ReminderIntervalMinutes = (int)controls.ReminderIntervalMinutes.Value;
    }

    private async Task SaveAsync()
    {
        try
        {
            ApplyControlsToBundle();
            ConfigStore.Save(_configPath, _bundle);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"No se pudo guardar la configuración:\r\n\r\n{ex.Message}",
                "ResourceAlerter Viewer", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var restart = MessageBox.Show(
            "Configuración guardada. ¿Reiniciar el servicio ResourceAlerter ahora para aplicar los cambios? (Va a pedir elevación)",
            "ResourceAlerter Viewer", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

        if (restart == DialogResult.Yes)
        {
            await ServiceRestarter.RestartElevatedAsync(this);
        }

        Close();
    }
}
