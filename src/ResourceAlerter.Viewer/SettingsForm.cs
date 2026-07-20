using ResourceAlerter.Configuration;
using ResourceAlerter.Localization;
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
    private ComboBox _languageBox = null!;

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

        Text = Strings.T("ResourceAlerter — Configuración", "ResourceAlerter — Settings");
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
        tabs.TabPages.Add(BuildMonitorTab("CPU", "CPU", _bundle.Monitoring.Cpu));
        tabs.TabPages.Add(BuildMonitorTab("Memory", Strings.T("Memoria", "Memory"), _bundle.Monitoring.Memory));
        tabs.TabPages.Add(BuildDiskTab());
        tabs.TabPages.Add(BuildTemperatureTab());
        tabs.TabPages.Add(BuildVoltageTab());
        tabs.TabPages.Add(BuildNetworkTab());
        tabs.TabPages.Add(BuildDatabaseTab());
        tabs.TabPages.Add(BuildLoggingTab());

        var buttonPanel = new Panel { Dock = DockStyle.Bottom, Height = 48 };
        var saveButton = new Button { Text = Strings.T("Guardar y reiniciar servicio", "Save and restart service"), Width = 200, Height = 32, Left = 12, Top = 8 };
        var cancelButton = new Button { Text = Strings.T("Cancelar", "Cancel"), Width = 90, Height = 32, Left = 220, Top = 8 };
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
        var panel = NewPanel(4);
        _machineNameBox = AddText(panel, 0, Strings.T("Nombre de máquina (mails/logs):", "Machine name (mail/logs):"), _bundle.General.MachineName ?? "");
        _pollingIntervalBox = AddNumeric(panel, 1, Strings.T("Intervalo de sondeo (segundos):", "Polling interval (seconds):"), _bundle.Monitoring.PollingIntervalSeconds, 1, 3600);

        panel.Controls.Add(new Label
        {
            Text = Strings.T("Idioma (interfaz y alertas):", "Language (UI and alerts):"),
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(3, 8, 10, 3),
        }, 0, 2);
        _languageBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150, Anchor = AnchorStyles.Left };
        _languageBox.Items.Add(new LanguageItem("es", "Español"));
        _languageBox.Items.Add(new LanguageItem("en", "English"));
        _languageBox.SelectedIndex = string.Equals(_bundle.General.Language, "en", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        panel.Controls.Add(_languageBox, 1, 2);

        var note = new Label
        {
            Text = Strings.T(
                "Dejar el nombre de máquina vacío usa el nombre real de Windows.\nEsto no afecta el archivo appsettings.<MAQUINA>.json, que siempre usa el nombre real.\nEl idioma se aplica a la interfaz del Viewer (al reabrirlo) y a los mails/Discord (al reiniciar el servicio).",
                "Leaving the machine name empty uses the real Windows name.\nThis does not affect the appsettings.<MACHINE>.json file, which always uses the real name.\nLanguage applies to the Viewer UI (next time it opens) and to mail/Discord alerts (once the service restarts)."),
            AutoSize = true,
            ForeColor = Color.Gray,
        };
        panel.Controls.Add(note, 0, 3);
        panel.SetColumnSpan(note, 2);
        page.Controls.Add(panel);
        return page;
    }

    private sealed record LanguageItem(string Code, string DisplayName)
    {
        public override string ToString() => DisplayName;
    }

    private TabPage BuildSmtpTab()
    {
        var page = new TabPage("SMTP");
        var panel = NewPanel(12);
        _smtpHostBox = AddText(panel, 0, Strings.T("Servidor (Host):", "Server (Host):"), _bundle.Smtp.Host);
        _smtpPortBox = AddNumeric(panel, 1, Strings.T("Puerto:", "Port:"), _bundle.Smtp.Port, 1, 65535);
        _smtpUseSslBox = AddCheck(panel, 2, Strings.T("Usar SSL/TLS:", "Use SSL/TLS:"), _bundle.Smtp.UseSsl);
        _smtpRequiresAuthBox = AddCheck(panel, 3, Strings.T("Requiere autenticación:", "Requires authentication:"), _bundle.Smtp.RequiresAuthentication);
        _smtpUsernameBox = AddText(panel, 4, Strings.T("Usuario:", "Username:"), _bundle.Smtp.Username);
        _smtpPasswordBox = AddText(panel, 5, Strings.T("Contraseña:", "Password:"), _bundle.Smtp.Password, password: true);
        _smtpFromAddressBox = AddText(panel, 6, Strings.T("Dirección remitente:", "From address:"), _bundle.Smtp.FromAddress);
        _smtpFromDisplayNameBox = AddText(panel, 7, Strings.T("Nombre remitente:", "From display name:"), _bundle.Smtp.FromDisplayName);
        _smtpRecipientsBox = AddMultilineText(panel, 8, Strings.T("Destinatarios (uno por línea):", "Recipients (one per line):"), JoinLines(_bundle.Smtp.Recipients));
        _smtpRetryCountBox = AddNumeric(panel, 9, Strings.T("Reintentos de envío:", "Send retries:"), _bundle.Smtp.RetryCount, 1, 10);
        _smtpRetryBackoffBox = AddNumeric(panel, 10, Strings.T("Espera entre reintentos (segundos):", "Wait between retries (seconds):"), _bundle.Smtp.RetryBackoffSeconds, 1, 300);
        _smtpTimeoutBox = AddNumeric(panel, 11, Strings.T("Timeout (milisegundos):", "Timeout (milliseconds):"), _bundle.Smtp.TimeoutMilliseconds, 1000, 300_000);
        page.Controls.Add(panel);
        return page;
    }

    private TabPage BuildDiscordTab()
    {
        var page = new TabPage("Discord");
        var panel = NewPanel(3);
        _discordEnabledBox = AddCheck(panel, 0, Strings.T("Habilitado:", "Enabled:"), _bundle.Discord.Enabled);
        _discordWebhookBox = AddText(panel, 1, Strings.T("URL del webhook:", "Webhook URL:"), _bundle.Discord.WebhookUrl);
        var note = new Label
        {
            Text = Strings.T(
                "Configuración del canal de Discord → Integraciones → Webhooks. No hace falta bot.\nSe manda en paralelo al mail, sin adjuntos (gráficos/logs quedan solo en el mail).",
                "Discord channel Settings → Integrations → Webhooks. No bot needed.\nSent in parallel with mail, without attachments (charts/logs stay mail-only)."),
            AutoSize = true,
            ForeColor = Color.Gray,
        };
        panel.Controls.Add(note, 0, 2);
        panel.SetColumnSpan(note, 2);
        page.Controls.Add(panel);
        return page;
    }

    private TabPage BuildMonitorTab(string key, string displayTitle, MonitorOptionsBase options)
    {
        var page = new TabPage(displayTitle);
        var panel = NewPanel(6);
        var row = 0;

        var controls = new MonitorControls { Enabled = AddCheck(panel, row++, Strings.T("Habilitado:", "Enabled:"), options.Enabled) };

        row = AddMonitorSpecificFields(panel, row, options);

        controls.SustainedWindowSeconds = AddNumeric(panel, row++, Strings.T("Ventana sostenida (segundos):", "Sustained window (seconds):"), options.SustainedWindowSeconds, 1, 3600);
        controls.RecoveryWindowSeconds = AddNumeric(panel, row++, Strings.T("Ventana de recuperación (segundos):", "Recovery window (seconds):"), options.RecoveryWindowSeconds, 1, 3600);
        controls.ReminderIntervalMinutes = AddNumeric(panel, row, Strings.T("Recordatorio cada (minutos):", "Reminder every (minutes):"), options.ReminderIntervalMinutes, 1, 1440);

        _monitorControls[key] = controls;
        page.Controls.Add(panel);
        return page;
    }

    /// <summary>CPU/Memory share the alert+recovery threshold shape; other monitors have their own tabs/fields.</summary>
    private int AddMonitorSpecificFields(TableLayoutPanel panel, int row, MonitorOptionsBase options)
    {
        if (options is CpuOptions cpu)
        {
            var alert = AddNumeric(panel, row++, Strings.T("Umbral de alerta (%):", "Alert threshold (%):"), (decimal)cpu.AlertThresholdPercent, 0, 100, 1);
            var recovery = AddNumeric(panel, row++, Strings.T("Umbral de recuperación (%):", "Recovery threshold (%):"), (decimal)cpu.RecoveryThresholdPercent, 0, 100, 1);
            _cpuAlert = alert;
            _cpuRecovery = recovery;
        }
        else if (options is MemoryOptions mem)
        {
            var alert = AddNumeric(panel, row++, Strings.T("Umbral de alerta (%):", "Alert threshold (%):"), (decimal)mem.AlertThresholdPercent, 0, 100, 1);
            var recovery = AddNumeric(panel, row++, Strings.T("Umbral de recuperación (%):", "Recovery threshold (%):"), (decimal)mem.RecoveryThresholdPercent, 0, 100, 1);
            _memAlert = alert;
            _memRecovery = recovery;
        }

        return row;
    }

    private NumericUpDown _cpuAlert = null!, _cpuRecovery = null!, _memAlert = null!, _memRecovery = null!;

    private TabPage BuildDiskTab()
    {
        var page = new TabPage(Strings.T("Disco", "Disk"));
        var panel = NewPanel(8);
        var disk = _bundle.Monitoring.Disk;

        var controls = new MonitorControls { Enabled = AddCheck(panel, 0, Strings.T("Habilitado:", "Enabled:"), disk.Enabled) };
        _diskPercentThresholdBox = AddNumeric(panel, 1, Strings.T("Umbral de espacio libre (%):", "Free space threshold (%):"), (decimal)disk.FreeSpacePercentThreshold, 0, 100, 1);
        _diskAbsoluteGbBox = AddNumeric(panel, 2, Strings.T("Umbral de espacio libre (GB):", "Free space threshold (GB):"), (decimal)disk.FreeSpaceAbsoluteGbThreshold, 0, 10_000, 1);
        _diskDrivesBox = AddText(panel, 3, Strings.T("Unidades a vigilar (ej: D: o C:, D:):", "Drives to watch (e.g. D: or C:, D:):"), JoinCsv(disk.Drives));
        controls.SustainedWindowSeconds = AddNumeric(panel, 4, Strings.T("Ventana sostenida (segundos):", "Sustained window (seconds):"), disk.SustainedWindowSeconds, 1, 3600);
        controls.RecoveryWindowSeconds = AddNumeric(panel, 5, Strings.T("Ventana de recuperación (segundos):", "Recovery window (seconds):"), disk.RecoveryWindowSeconds, 1, 3600);
        controls.ReminderIntervalMinutes = AddNumeric(panel, 6, Strings.T("Recordatorio cada (minutos):", "Reminder every (minutes):"), disk.ReminderIntervalMinutes, 1, 1440);

        var note = new Label
        {
            Text = Strings.T(
                "Vacío = solo la unidad de sistema (normalmente C:). Si ponés una o más unidades acá,\n" +
                "REEMPLAZAN a la de sistema — no se suman. Por ej: poné solo \"D:\" para vigilar la unidad\n" +
                "de temporales/swap en vez de C:, o \"C:, D:\" para vigilar ambas.",
                "Empty = system drive only (normally C:). If you list one or more drives here, they\n" +
                "REPLACE the system drive — they don't add to it. E.g. put just \"D:\" to watch the\n" +
                "temp/swap drive instead of C:, or \"C:, D:\" to watch both."),
            AutoSize = true,
            ForeColor = Color.Gray,
        };
        panel.Controls.Add(note, 0, 7);
        panel.SetColumnSpan(note, 2);

        _monitorControls["Disk"] = controls;
        page.Controls.Add(panel);
        return page;
    }

    private TabPage BuildTemperatureTab()
    {
        var page = new TabPage(Strings.T("Temperatura", "Temperature"));
        var panel = NewPanel(6);
        var temp = _bundle.Monitoring.Temperature;

        var controls = new MonitorControls { Enabled = AddCheck(panel, 0, Strings.T("Habilitado:", "Enabled:"), temp.Enabled) };
        _temperatureThresholdBox = AddNumeric(panel, 1, Strings.T("Umbral de alerta (°C):", "Alert threshold (°C):"), (decimal)temp.AlertThresholdCelsius, 0, 150, 1);
        controls.SustainedWindowSeconds = AddNumeric(panel, 2, Strings.T("Ventana sostenida (segundos):", "Sustained window (seconds):"), temp.SustainedWindowSeconds, 1, 3600);
        controls.RecoveryWindowSeconds = AddNumeric(panel, 3, Strings.T("Ventana de recuperación (segundos):", "Recovery window (seconds):"), temp.RecoveryWindowSeconds, 1, 3600);
        controls.ReminderIntervalMinutes = AddNumeric(panel, 4, Strings.T("Recordatorio cada (minutos):", "Reminder every (minutes):"), temp.ReminderIntervalMinutes, 1, 1440);

        _monitorControls["Temperature"] = controls;
        page.Controls.Add(panel);
        return page;
    }

    private TabPage BuildVoltageTab()
    {
        var page = new TabPage(Strings.T("Voltaje", "Voltage"));
        var panel = NewPanel(8);
        var volt = _bundle.Monitoring.Voltage;

        var controls = new MonitorControls { Enabled = AddCheck(panel, 0, Strings.T("Habilitado:", "Enabled:"), volt.Enabled) };
        var deviationPercent = (decimal)(volt.AllowedDeviationFraction * 100);
        _voltageDeviationBox = AddNumeric(panel, 1, Strings.T("Desviación permitida (%):", "Allowed deviation (%):"), deviationPercent, 0, 100, 1);
        _voltageRailsBox = AddMultilineText(panel, 2, Strings.T("Rieles nominales (Nombre=Voltios):", "Nominal rails (Name=Volts):"),
            JoinKeyValue(volt.NominalRails.Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value.ToString("0.##")))));
        _voltageOverridesBox = AddMultilineText(panel, 3, Strings.T("Nombres de sensor reales (Riel=Sensor, opcional):", "Real sensor names (Rail=Sensor, optional):"),
            JoinKeyValue(volt.SensorNameOverrides), height: 50);
        controls.SustainedWindowSeconds = AddNumeric(panel, 4, Strings.T("Ventana sostenida (segundos):", "Sustained window (seconds):"), volt.SustainedWindowSeconds, 1, 3600);
        controls.RecoveryWindowSeconds = AddNumeric(panel, 5, Strings.T("Ventana de recuperación (segundos):", "Recovery window (seconds):"), volt.RecoveryWindowSeconds, 1, 3600);
        controls.ReminderIntervalMinutes = AddNumeric(panel, 6, Strings.T("Recordatorio cada (minutos):", "Reminder every (minutes):"), volt.ReminderIntervalMinutes, 1, 1440);

        var note = new Label
        {
            Text = Strings.T(
                "Usá \"ResourceAlerter.exe --list-sensors\" (elevado) para ver los nombres reales de sensores.",
                "Use \"ResourceAlerter.exe --list-sensors\" (elevated) to see the real sensor names."),
            AutoSize = true,
            ForeColor = Color.Gray,
        };
        panel.Controls.Add(note, 0, 7);
        panel.SetColumnSpan(note, 2);

        _monitorControls["Voltage"] = controls;
        page.Controls.Add(panel);
        return page;
    }

    private NumericUpDown _voltageDeviationBox = null!;

    private TabPage BuildNetworkTab()
    {
        var page = new TabPage(Strings.T("Red", "Network"));
        var panel = NewPanel(11);
        var net = _bundle.Monitoring.Network;

        var controls = new MonitorControls { Enabled = AddCheck(panel, 0, Strings.T("Habilitado:", "Enabled:"), net.Enabled) };
        _networkTargetHostBox = AddText(panel, 1, Strings.T("Host a pingear (vacío = auto-detectar gateway):", "Host to ping (empty = auto-detect gateway):"), net.TargetHost ?? "");
        _networkFallbackHostBox = AddText(panel, 2, Strings.T("Host de respaldo:", "Fallback host:"), net.FallbackHost);
        _networkPingIntervalBox = AddNumeric(panel, 3, Strings.T("Intervalo de ping (segundos):", "Ping interval (seconds):"), net.PingIntervalSeconds, 1, 3600);
        _networkPingTimeoutBox = AddNumeric(panel, 4, Strings.T("Timeout de ping (milisegundos):", "Ping timeout (milliseconds):"), net.PingTimeoutMilliseconds, 100, 60_000);
        _networkWindowSizeBox = AddNumeric(panel, 5, Strings.T("Tamaño de ventana (cantidad de pings):", "Window size (number of pings):"), net.WindowSize, 1, 100);
        _networkMaxLossesBox = AddNumeric(panel, 6, Strings.T("Máx. pérdidas en la ventana:", "Max losses in window:"), net.MaxLossesInWindow, 0, 100);
        _networkMaxOutageBox = AddNumeric(panel, 7, Strings.T("Máx. corte continuo (segundos):", "Max continuous outage (seconds):"), net.MaxConsecutiveOutageSeconds, 1, 3600);
        controls.SustainedWindowSeconds = AddNumeric(panel, 8, Strings.T("Ventana sostenida (segundos):", "Sustained window (seconds):"), net.SustainedWindowSeconds, 1, 3600);
        controls.RecoveryWindowSeconds = AddNumeric(panel, 9, Strings.T("Ventana de recuperación (segundos):", "Recovery window (seconds):"), net.RecoveryWindowSeconds, 1, 3600);
        _networkReminderBox = AddNumeric(panel, 10, Strings.T("Recordatorio cada (minutos):", "Reminder every (minutes):"), net.ReminderIntervalMinutes, 1, 1440);

        _monitorControls["Network"] = controls;
        page.Controls.Add(panel);
        return page;
    }

    private NumericUpDown _networkReminderBox = null!;

    private TabPage BuildDatabaseTab()
    {
        var page = new TabPage(Strings.T("Base de datos", "Database"));
        var panel = NewPanel(2);
        _dbPathBox = AddText(panel, 0, Strings.T("Ruta del archivo SQLite:", "SQLite file path:"), _bundle.Database.Path);
        _dbRetentionBox = AddNumeric(panel, 1, Strings.T("Retención de datos (días):", "Data retention (days):"), _bundle.Database.RetentionDays, 1, 3650);
        page.Controls.Add(panel);
        return page;
    }

    private TabPage BuildLoggingTab()
    {
        var page = new TabPage("Logs");
        var panel = NewPanel(3);
        _logDirectoryBox = AddText(panel, 0, Strings.T("Carpeta de logs:", "Log folder:"), _bundle.FileLogging.Directory);
        _logMaxSizeBox = AddNumeric(panel, 1, Strings.T("Tamaño máximo por archivo (MB):", "Max size per file (MB):"), _bundle.FileLogging.MaxFileSizeMb, 1, 1000);
        _logRetentionBox = AddNumeric(panel, 2, Strings.T("Retención de logs (días):", "Log retention (days):"), _bundle.FileLogging.RetentionDays, 1, 3650);
        page.Controls.Add(panel);
        return page;
    }

    private void ApplyControlsToBundle()
    {
        _bundle.General.MachineName = string.IsNullOrWhiteSpace(_machineNameBox.Text) ? null : _machineNameBox.Text.Trim();
        _bundle.General.Language = ((LanguageItem)_languageBox.SelectedItem!).Code;
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

        ApplyMonitorBase(_bundle.Monitoring.Memory, "Memory");
        _bundle.Monitoring.Memory.AlertThresholdPercent = (double)_memAlert.Value;
        _bundle.Monitoring.Memory.RecoveryThresholdPercent = (double)_memRecovery.Value;

        ApplyMonitorBase(_bundle.Monitoring.Disk, "Disk");
        _bundle.Monitoring.Disk.FreeSpacePercentThreshold = (double)_diskPercentThresholdBox.Value;
        _bundle.Monitoring.Disk.FreeSpaceAbsoluteGbThreshold = (double)_diskAbsoluteGbBox.Value;
        _bundle.Monitoring.Disk.Drives = SplitCsv(_diskDrivesBox.Text);

        ApplyMonitorBase(_bundle.Monitoring.Temperature, "Temperature");
        _bundle.Monitoring.Temperature.AlertThresholdCelsius = (double)_temperatureThresholdBox.Value;

        ApplyMonitorBase(_bundle.Monitoring.Voltage, "Voltage");
        _bundle.Monitoring.Voltage.AllowedDeviationFraction = (double)(_voltageDeviationBox.Value / 100m);
        _bundle.Monitoring.Voltage.NominalRails = SplitKeyValue(_voltageRailsBox.Text)
            .ToDictionary(kv => kv.Key, kv => double.TryParse(kv.Value, out var v) ? v : 0.0);
        _bundle.Monitoring.Voltage.SensorNameOverrides = SplitKeyValue(_voltageOverridesBox.Text);

        var netControls = _monitorControls["Network"];
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
            MessageBox.Show(Strings.T($"No se pudo guardar la configuración:\r\n\r\n{ex.Message}", $"Could not save the configuration:\r\n\r\n{ex.Message}"),
                "ResourceAlerter Viewer", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var restart = MessageBox.Show(
            Strings.T(
                "Configuración guardada. ¿Reiniciar el servicio ResourceAlerter ahora para aplicar los cambios? (Va a pedir elevación)",
                "Configuration saved. Restart the ResourceAlerter service now to apply the changes? (This will prompt for elevation)"),
            "ResourceAlerter Viewer", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

        if (restart == DialogResult.Yes)
        {
            await ServiceRestarter.RestartElevatedAsync(this);
        }

        Close();
    }
}
