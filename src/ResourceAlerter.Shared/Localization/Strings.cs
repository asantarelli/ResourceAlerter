namespace ResourceAlerter.Localization;

/// <summary>
/// All user-facing text (Viewer UI + mail/Discord alert content), in English and Spanish.
/// Internal file/console logs are NOT covered here — they stay in English regardless of
/// <see cref="CurrentLanguage"/>, since they're technical diagnostics, not something sent to
/// end users. Set <see cref="CurrentLanguage"/> once at startup from
/// <see cref="Configuration.GeneralOptions.Language"/> before building any alert text.
///
/// Deliberately a plain "IsEs ? a : b" ternary class rather than .resx/satellite assemblies:
/// this project has been repeatedly bitten by things that work in a normal build but break in
/// a self-contained single-file publish (native libs needing extraction, in particular) —
/// satellite resource assemblies are exactly that kind of risk, and a plain C# file has none of
/// it since it's just compiled straight into the same assembly.
/// </summary>
public static class Strings
{
    /// <summary>"es" or "en". Anything else falls back to Spanish.</summary>
    public static string CurrentLanguage { get; set; } = "es";

    private static bool IsEs => !CurrentLanguage.Equals("en", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Formats a number independent of the OS/thread culture — this app runs with
    /// InvariantGlobalization enabled, so constructing a real "es-ES" CultureInfo would throw;
    /// this just formats with InvariantCulture (period decimal) and swaps in a comma for
    /// Spanish. Use this for EVERY numeric value that ends up in mail/Discord/Viewer text —
    /// plain "{value:F1}" interpolation uses the ambient thread culture, which has nothing to
    /// do with the user's chosen language and was producing comma-decimals even in English mode
    /// on a Spanish-locale machine.
    /// </summary>
    public static string FormatNumber(double value, string format = "0.##")
    {
        var formatted = value.ToString(format, System.Globalization.CultureInfo.InvariantCulture);
        return IsEs ? formatted.Replace('.', ',') : formatted;
    }

    /// <summary>
    /// A percentage, formatted manually rather than via the "P" format specifier — "P" also
    /// varies the "%" symbol's spacing/position by culture, which InvariantGlobalization makes
    /// just as unreliable as the decimal separator.
    /// </summary>
    public static string FormatPercent(double fraction, string decimalsFormat = "F0") =>
        $"{FormatNumber(fraction * 100, decimalsFormat)}%";

    /// <summary>
    /// Inline translate helper for one-off UI strings (mainly the Viewer's Settings screen,
    /// which has ~100 labels not worth a named property each) — keeps every user-facing string
    /// routed through <see cref="CurrentLanguage"/> without the ceremony of a dedicated method.
    /// </summary>
    public static string T(string es, string en) => IsEs ? es : en;

    /// <summary>
    /// Display name for a monitor's internal <c>Name</c> (which stays fixed/English — it's a
    /// database/dictionary key, e.g. recorded sample rows and the alert-tracker lookup — this
    /// only affects what's shown to a human).
    /// </summary>
    public static string MonitorDisplayName(string internalName) => internalName switch
    {
        "CPU" => "CPU",
        "Memory" => IsEs ? "Memoria" : "Memory",
        "Disk" => IsEs ? "Disco" : "Disk",
        "Temperature" => IsEs ? "Temperatura" : "Temperature",
        "Voltage" => IsEs ? "Voltaje" : "Voltage",
        "Network" => IsEs ? "Red" : "Network",
        _ => internalName,
    };

    public static string NotAvailable => IsEs ? "N/D" : "N/A";

    // ---- Common mail body labels ----
    public static string Label_Machine => IsEs ? "Máquina" : "Machine";
    public static string Label_Version => IsEs ? "Versión" : "Version";
    public static string Label_Monitor => IsEs ? "Monitor" : "Monitor";
    public static string Label_Item => IsEs ? "Elemento" : "Subject";
    public static string Label_DetectedValue => IsEs ? "Valor detectado" : "Detected value";
    public static string Label_CurrentValue => IsEs ? "Valor actual" : "Current value";
    public static string Label_LastDetectedValue => IsEs ? "Último valor detectado" : "Last detected value";
    public static string Label_Threshold => IsEs ? "Umbral" : "Threshold";
    public static string Label_EventStarted => IsEs ? "Evento iniciado" : "Event started";
    public static string Label_OngoingFor => IsEs ? "Activo desde hace" : "Ongoing for";
    public static string Label_ResolvedAt => IsEs ? "Resuelto" : "Resolved at";
    public static string Label_TotalDuration => IsEs ? "Duración total" : "Total duration";
    public static string Label_StartedAt => IsEs ? "Iniciado" : "Started at";
    public static string Word_Threshold => IsEs ? "umbral" : "threshold";
    public static string Word_StillActive => IsEs ? "SIGUE ACTIVA" : "STILL ACTIVE";

    // ---- Mail subjects ----
    public static string Subject_Alert(string machine, string monitor, string item) =>
        IsEs ? $"[{machine}] ALERTA: {monitor} - {item}" : $"[{machine}] ALERT: {monitor} - {item}";

    public static string Subject_StillActive(string machine, string monitor, string item) =>
        IsEs ? $"[{machine}] SIGUE ACTIVA: {monitor} - {item}" : $"[{machine}] STILL ACTIVE: {monitor} - {item}";

    public static string Subject_Resolved(string machine, string monitor, string item) =>
        IsEs ? $"[{machine}] RESUELTA: {monitor} - {item}" : $"[{machine}] RESOLVED: {monitor} - {item}";

    public static string Subject_ServiceStarted(string machine, bool warning) => warning
        ? (IsEs ? $"[{machine}] ADVERTENCIA: servicio ResourceAlerter iniciado" : $"[{machine}] WARNING: ResourceAlerter service started")
        : (IsEs ? $"[{machine}] Servicio ResourceAlerter iniciado" : $"[{machine}] ResourceAlerter service started");

    public static string Subject_DailySummary(string machine, DateTime date, int alertCount) =>
        IsEs
            ? $"[{machine}] Resumen diario {date:yyyy-MM-dd} — {alertCount} alerta(s)"
            : $"[{machine}] Daily summary {date:yyyy-MM-dd} — {alertCount} alert(s)";

    // ---- Startup mail (Worker) ----
    public static string Startup_Footer => IsEs
        ? "Este es un mensaje informativo enviado cada vez que el servicio arranca (incluso tras " +
          "un reinicio) — no requiere ninguna acción salvo que no lo esperaras."
        : "This is an informational message sent whenever the service starts (including after a " +
          "reboot) — no action required unless it was unexpected.";

    public static string Startup_RecordingWarning(string? error) => IsEs
        ? $"\r\n*** ADVERTENCIA: la grabación de datos (SQLite) no pudo iniciar: {error} ***\r\n" +
          "*** Los gráficos, el Viewer y el resumen diario no van a tener datos hasta que se solucione. ***\r\n" +
          "*** Revisá el log para ver el error completo; suele ser un problema de empaquetado de una librería nativa. ***\r\n"
        : $"\r\n*** WARNING: data recording (SQLite) failed to start: {error} ***\r\n" +
          "*** Charts, the Viewer app, and the daily summary will have no data until this is fixed. ***\r\n" +
          "*** Check the log for the full exception; this usually means a native-library packaging problem. ***\r\n";

    public static string Startup_ActivelyMonitoring => IsEs ? "Monitoreando activamente:" : "Actively monitoring:";
    public static string Startup_NothingCheckConfig => IsEs ? "  (nada — revisá la configuración)" : "  (nothing — check configuration)";
    public static string Startup_NotMonitoredHeader => IsEs
        ? "No monitoreado en esta máquina (no se van a enviar alertas para esto):"
        : "Not monitored on this machine (no alerts will be sent for these):";
    public static string Startup_DisabledInConfig => IsEs ? "deshabilitado en la configuración" : "disabled in configuration";

    // ---- Daily summary mail ----
    public static string DailySummary_For => IsEs ? "Resumen diario del" : "Daily summary for";
    public static string DailySummary_Period => IsEs ? "Período" : "Period";
    public static string DailySummary_NoAlerts => IsEs ? "No hubo alertas en las últimas 24 horas." : "No alerts in the last 24 hours.";
    public static string DailySummary_AlertsHeader(int count) =>
        IsEs ? $"Alertas en las últimas 24 horas ({count}):" : $"Alerts in the last 24 hours ({count}):";
    public static string DailySummary_AttachedFooter => IsEs
        ? "Adjunto: un gráfico por variable monitoreada (líneas verticales rojas marcan el inicio de cada " +
          "alerta), el/los archivo(s) de log del día, y hardware-report.txt — el diagnóstico completo de " +
          "LibreHardwareMonitor para esta máquina (modelo de motherboard/BIOS y todos los sensores que " +
          "expone), para planificar qué sensores soportar a futuro."
        : "Attached: one chart per monitored variable (red vertical lines mark alert starts), the day's " +
          "log file(s), and hardware-report.txt — the full LibreHardwareMonitor diagnostic dump for this " +
          "machine (motherboard/BIOS model plus every sensor it exposes), for planning which sensors to " +
          "support next.";

    // ---- Monitor result text ----
    public static string Monitor_RecoveryBelow(double percent) =>
        IsEs ? $"(recuperación por debajo de {FormatNumber(percent)}%)" : $"(recovery below {FormatNumber(percent)}%)";

    public static string Cpu_Subject => "Total"; // same word in both languages

    public static string Memory_Subject => IsEs ? "RAM física" : "Physical RAM";

    /// <summary>
    /// Fixed/English — this is <see cref="Monitors.MonitorResult.Subject"/>, the value recorded
    /// into SQLite and used as the AlertStateTracker per-subject dictionary key, so it must NOT
    /// change with <see cref="CurrentLanguage"/> (same reasoning as <see cref="MonitorDisplayName"/>
    /// for the monitor Name). Before this existed, <see cref="Memory_Subject"/> — a translated
    /// string — was used directly as the Subject key, so switching Language produced a second,
    /// permanently-orphaned series in the Viewer (v3.5.0 bug, fixed in v3.5.1). Use
    /// <see cref="SubjectDisplayName"/> to translate this back for display.
    /// </summary>
    public const string Memory_SubjectKey = "Physical RAM";
    public static string Memory_Used(double percent, string usedBytes, string totalBytes) =>
        IsEs ? $"{FormatNumber(percent, "F1")}% usado ({usedBytes} / {totalBytes})" : $"{FormatNumber(percent, "F1")}% used ({usedBytes} / {totalBytes})";

    public static string Disk_Free(double gb, double percent) =>
        IsEs ? $"{FormatNumber(gb, "F1")} GB libres ({FormatNumber(percent, "F1")}%)" : $"{FormatNumber(gb, "F1")} GB free ({FormatNumber(percent, "F1")}%)";
    public static string Disk_ThresholdBelow(double percent, double gb) =>
        IsEs ? $"por debajo de {FormatNumber(percent)}% o {FormatNumber(gb)} GB" : $"below {FormatNumber(percent)}% or {FormatNumber(gb)} GB";

    public static string Voltage_OffNominal(double volts, double deviation) =>
        IsEs ? $"{FormatNumber(volts, "F2")}V ({FormatPercent(deviation, "F1")} de desvío)" : $"{FormatNumber(volts, "F2")}V ({FormatPercent(deviation, "F1")} off nominal)";

    public static string Network_LossesInWindow(int losses, int windowCount) =>
        IsEs ? $"{losses}/{windowCount} pérdidas en la ventana" : $"{losses}/{windowCount} losses in window";
    public static string Network_Outage(double seconds) =>
        IsEs ? $", corte de {FormatNumber(seconds, "F0")}s" : $", outage {FormatNumber(seconds, "F0")}s";
    public static string Network_Threshold(int maxLosses, int windowSize, int maxOutageSeconds) =>
        IsEs
            ? $">{maxLosses} pérdidas/{windowSize} o >{maxOutageSeconds}s de corte"
            : $">{maxLosses} losses/{windowSize} or >{maxOutageSeconds}s outage";

    // ---- "Sensor unavailable" reasons (shown to the admin in the startup mail) ----
    public static string Unavailable_ProcessorCounterInit => IsEs
        ? "No se pudo inicializar el contador de rendimiento del procesador."
        : "Processor performance counter could not be initialized.";
    public static string Unavailable_ProcessorCounterRead => IsEs
        ? "No se pudo leer el contador de rendimiento del procesador."
        : "Failed to read the processor performance counter.";
    public static string Unavailable_MemoryStatus => IsEs
        ? "No se pudo leer el estado de la memoria física."
        : "Failed to read physical memory status.";
    public static string Unavailable_DriveNotReady => IsEs ? "La unidad no está lista." : "Drive is not ready.";
    public static string Unavailable_DriveReadFailed(string error) =>
        IsEs ? $"No se pudo leer la unidad: {error}" : $"Failed to read drive: {error}";
    public static string Unavailable_HardwareMonitorClosed => IsEs
        ? "No se pudo abrir LibreHardwareMonitor (problema de driver o privilegios)."
        : "LibreHardwareMonitor could not open (driver/privilege issue).";
    public static string Unavailable_NoCpuTempSensors => IsEs
        ? "Este hardware no expone sensores de temperatura de CPU."
        : "No CPU temperature sensors are exposed by this hardware.";
    public static string Unavailable_NoVoltageSensor => IsEs
        ? "No se encontró un sensor de voltaje que coincida en este hardware."
        : "No matching voltage sensor found on this hardware.";

    // ---- Monitor subject labels ----
    public static string Temperature_CpuPackage => IsEs ? "CPU (paquete)" : "CPU Package";
    public static string Temperature_CpuAvgOfCores => IsEs ? "CPU (promedio de núcleos)" : "CPU (avg of cores)";

    /// <summary>Fixed/English Subject keys — see <see cref="Memory_SubjectKey"/> for why.</summary>
    public const string Temperature_CpuPackageKey = "CPU Package";
    public const string Temperature_CpuAvgOfCoresKey = "CPU (avg of cores)";

    /// <summary>
    /// Translates a stable <see cref="Monitors.MonitorResult.Subject"/> key back to display text
    /// for mail/Discord/Viewer/logs — the human-facing counterpart of <see cref="MonitorDisplayName"/>.
    /// Most monitors' Subject is already language-independent (CPU's fixed "Total", disk drive
    /// letters, PSU rail names, network hostnames) and passes through unchanged.
    /// </summary>
    public static string SubjectDisplayName(string monitorInternalName, string subjectKey) =>
        (monitorInternalName, subjectKey) switch
        {
            ("Memory", Memory_SubjectKey) => Memory_Subject,
            ("Temperature", Temperature_CpuPackageKey) => Temperature_CpuPackage,
            ("Temperature", Temperature_CpuAvgOfCoresKey) => Temperature_CpuAvgOfCores,
            _ => subjectKey,
        };

    // ---- Viewer (MainForm) ----
    public static string Viewer_Refresh => T("Refrescar", "Refresh");
    public static string Viewer_SendTodaySummary => T("Enviar resumen de hoy", "Send today's summary");
    public static string Viewer_Sending => T("Enviando...", "Sending...");
    public static string Viewer_Settings => T("Configuración", "Settings");
    public static string Viewer_AutoRefreshEvery(double seconds) =>
        T($"Auto-actualiza cada {FormatNumber(seconds, "F0")}s", $"Auto-refresh every {FormatNumber(seconds, "F0")}s");
    public static string Viewer_ExeNotFound(string exePath) => T(
        $"No se encontró {exePath}. ¿Está instalado el servicio en esta misma carpeta?",
        $"Could not find {exePath}. Is the service installed in this same folder?");
    public static string Viewer_SummarySentOk => T("El resumen de hoy se envió correctamente.", "Today's summary was sent successfully.");
    public static string Viewer_SummarySendFailed => T(
        "El envío falló. Revisá el log del servicio (logs\\resourcealerter-*.log, carpeta de instalación) para el detalle.",
        "Sending failed. Check the service log (logs\\resourcealerter-*.log, install folder) for details.");
    public static string Viewer_SummarySendError(string message) => T(
        $"No se pudo enviar el resumen:\r\n\r\n{message}", $"Could not send the summary:\r\n\r\n{message}");
    public static string Viewer_StartProcessFailed => T("No se pudo iniciar el proceso.", "Could not start the process.");
    public static string Viewer_DatabaseNotFound => T(
        "Base de datos no encontrada — ¿está corriendo el servicio?", "Database not found — is the service running?");
    public static string Viewer_NoDataRecorded => T("Sin datos registrados", "No data recorded");
    public static string Viewer_Error(string message) => T($"Error: {message}", $"Error: {message}");
    public static string Viewer_Last24Hours(string series) => T($"{series} — últimas 24 horas", $"{series} — last 24 hours");
    public static string Viewer_Title(string machine) => $"ResourceAlerter Viewer — {machine}";

    public static string Viewer_ServiceRestartedOk => T("Servicio reiniciado correctamente.", "Service restarted successfully.");
    public static string Viewer_ServiceRestartFailed(string serviceName) => T(
        $"El reinicio del servicio falló. Reiniciálo manualmente desde services.msc o con 'Restart-Service {serviceName}' en una PowerShell elevada.",
        $"Restarting the service failed. Restart it manually from services.msc or with 'Restart-Service {serviceName}' in an elevated PowerShell.");
    public static string Viewer_ServiceRestartError(string message) => T(
        $"No se pudo reiniciar el servicio:\r\n\r\n{message}", $"Could not restart the service:\r\n\r\n{message}");

    // ---- Internal .log messages ----
    // Translated because logs\resourcealerter-*.log gets attached to the daily summary mail
    // clients receive — these are no longer purely internal diagnostics. {PlaceholderName}
    // tokens must stay exactly as-is (untranslated) in both variants: ILogger binds them
    // positionally to the LogXxx(...) call's args, and the token text itself never appears in
    // the rendered log line — only the surrounding literal text does.
    public static string Log_WorkerStarting => T(
        "ResourceAlerter v{Version} iniciando en {Machine}. Intervalo de sondeo: {Interval}s. Monitores: {Monitors}",
        "ResourceAlerter v{Version} starting on {Machine}. Polling interval: {Interval}s. Monitors: {Monitors}");
    public static string Log_MonitorCheckThrew => T(
        "El monitor {Monitor} lanzó una excepción durante Check(); se omite este ciclo",
        "Monitor {Monitor} threw during Check(); skipping this cycle");
    public static string Log_AlertStateProcessingFailed => T(
        "Falló el procesamiento del estado de alerta para {Monitor}/{Subject}",
        "Failed processing alert state for {Monitor}/{Subject}");
    public static string Log_DailySummaryFailed => T(
        "Falló la generación/envío del mail de resumen diario", "Failed to build/send the daily summary mail");
    public static string Log_StartupNotificationFailed => T(
        "Falló el envío del mail de aviso de inicio", "Failed to send startup notification mail");

    public static string Log_DbInitFailed => T(
        "Falló la inicialización de la base de datos SQLite en {Path}; la grabación de datos queda deshabilitada para esta ejecución",
        "Failed to initialize the SQLite database at {Path}; data recording is disabled for this run");
    public static string Log_DbInitialized => T(
        "Grabación de datos SQLite inicializada en {Path} (retención {Days} días)",
        "SQLite data recording initialized at {Path} (retention {Days} days)");
    public static string Log_AclAdjustFailed => T(
        "No se pudo ajustar la ACL en {Directory}; es posible que el Viewer no pueda leer la base de datos sin elevación",
        "Could not adjust ACL on {Directory}; the Viewer may not be able to read the database without elevation");
    public static string Log_RecordSamplesFailed => T("Falló el registro de {Count} muestras", "Failed to record {Count} samples");
    public static string Log_RecordAlertStartFailed => T(
        "Falló el registro del inicio de alerta para {Monitor}/{Subject}", "Failed to record alert start for {Monitor}/{Subject}");
    public static string Log_RecordAlertResolvedFailed => T(
        "Falló el registro de la resolución de alerta para {Monitor}/{Subject}",
        "Failed to record alert resolution for {Monitor}/{Subject}");
    public static string Log_ListSeriesFailed => T("Falló el listado de series registradas", "Failed to list recorded series");
    public static string Log_ReadSamplesFailed => T(
        "Falló la lectura de muestras para {Monitor}/{Subject}", "Failed to read samples for {Monitor}/{Subject}");
    public static string Log_ReadAlertEventsFailed => T("Falló la lectura de eventos de alerta", "Failed to read alert events");
    public static string Log_PurgedRows => T(
        "Se purgaron {Count} filas de la base de datos con más de {Days} días",
        "Purged {Count} database rows older than {Days} days");
    public static string Log_PurgeFailed => T("Falló la purga de la base de datos", "Database purge failed");
    public static string Log_PrunedOrphanedSeries => T(
        "Se descartaron {Count} serie(s) que ya no se están monitoreando (cambio de configuración): {Series}",
        "Discarded {Count} series no longer being monitored (configuration changed): {Series}");
    public static string Log_PruneOrphanedSeriesFailed => T(
        "Falló la limpieza de series ya no monitoreadas", "Failed to prune series no longer being monitored");

    public static string Log_SensorUnavailableIgnored => T(
        "{Monitor}/{Subject}: sensor no disponible en este hardware ({Reason}); se ignora — no se va a enviar mail de alerta por esto.",
        "{Monitor}/{Subject} sensor not available on this hardware ({Reason}); ignoring — no alert mail will be sent for it.");
    public static string Log_AlertTriggered => T(
        "{Monitor}/{Subject} ALERTA: {Value} (umbral {Threshold})", "{Monitor}/{Subject} ALERT: {Value} (threshold {Threshold})");
    public static string Log_AlertResolved => T(
        "{Monitor}/{Subject} RESUELTA después de {Duration}", "{Monitor}/{Subject} RESOLVED after {Duration}");

    public static string Log_CpuCounterInitFailed => T(
        "No se pudo inicializar el contador de rendimiento del procesador", "Could not initialize CPU performance counter");
    public static string Log_CpuCounterReadFailed => T("Falló la lectura del contador de CPU", "Failed to read CPU counter");
    public static string Log_DiskReadFailed => T("Falló la lectura de uso de disco para {Drive}", "Failed to read disk usage for {Drive}");
    public static string Log_MemoryReadFailed => T("Falló la lectura del estado de memoria", "Failed to read memory status");

    public static string Log_HwOpenFailed => T(
        "Falló la apertura de LibreHardwareMonitor; el monitoreo de temperatura/voltaje no va a estar disponible. " +
        "Lo más probable es que el servicio necesite ejecutarse como LocalSystem para que cargue el driver de sensores.",
        "Failed to open LibreHardwareMonitor; temperature/voltage monitoring will be unavailable. " +
        "The service most likely needs to run as LocalSystem for the sensor driver to load.");
    public static string Log_HwReportUnavailable => T(
        "(LibreHardwareMonitor no está disponible en esta máquina — no hay reporte para generar)",
        "(LibreHardwareMonitor unavailable on this machine — no report to generate)");
    public static string Log_HwReportGenFailed => T(
        "Falló la generación del reporte de hardware para el resumen diario",
        "Failed to generate the hardware report for the daily summary");
    public static string Log_HwReportGenFailedText(string message) => T(
        $"(falló la generación del reporte de hardware: {message})", $"(hardware report generation failed: {message})");
    public static string Log_SensorEnumFailed => T(
        "Falló la enumeración de sensores {SensorType}", "Failed to enumerate {SensorType} sensors");
    public static string Log_HwCloseError => T(
        "Error al cerrar la instancia de LibreHardwareMonitor", "Error closing LibreHardwareMonitor computer instance");

    public static string Log_DailySummaryResult => T(
        "Resumen diario del {Date}: {Result} ({Alerts} alertas, {Attachments} adjuntos)",
        "Daily summary for {Date}: {Result} ({Alerts} alerts, {Attachments} attachments)");
    public static string Log_Sent => T("enviado", "sent");
    public static string Log_FailedToSend => T("FALLÓ al enviarse", "FAILED to send");
    public static string Log_ChartRenderFailed => T(
        "Falló la generación del gráfico para {Monitor}/{Subject}", "Failed to render chart for {Monitor}/{Subject}");
    public static string Log_HwReportAttachFailed => T(
        "Falló la inclusión del reporte de hardware", "Failed to attach the hardware report");
    public static string Log_LogAttachFailed => T(
        "Falló la inclusión del/los archivo(s) de log del día", "Failed to attach the day's log file(s)");

    public static string Log_DiscordUnexpectedFail => T(
        "El aviso a Discord falló inesperadamente para '{Subject}'", "Discord notification failed unexpectedly for '{Subject}'");
    public static string Log_NoRecipients => T(
        "No hay destinatarios SMTP configurados; se descarta la alerta '{Subject}'",
        "No SMTP recipients configured; dropping alert '{Subject}'");
    public static string Log_MailSent => T("Mail de alerta enviado: {Subject}", "Alert mail sent: {Subject}");
    public static string Log_MailRetrying => T(
        "Falló el envío del mail de alerta (intento {Attempt}/{Max}): {Subject}. Reintentando en {Delay}s.",
        "Failed to send alert mail (attempt {Attempt}/{Max}): {Subject}. Retrying in {Delay}s.");
    public static string Log_MailGivenUp => T(
        "Falló el envío del mail de alerta después de {Attempts} intentos: {Subject}. Se abandona.",
        "Failed to send alert mail after {Attempts} attempts: {Subject}. Giving up.");

    public static string Log_DiscordSent => T("Alerta enviada a Discord: {Subject}", "Discord alert sent: {Subject}");
    public static string Log_DiscordBadStatus => T(
        "El webhook de Discord devolvió {StatusCode} para '{Subject}'", "Discord webhook returned {StatusCode} for '{Subject}'");
    public static string Log_DiscordSendFailed => T(
        "Falló el envío de la alerta a Discord '{Subject}'", "Failed to send Discord alert '{Subject}'");
    public static string Discord_Truncated => T(
        "\r\n… (truncado — ver el mail para el detalle completo)", "\r\n… (truncated — see the e-mail for full detail)");

    public static string Log_PingFailed => T("Falló el ping a {Target}", "Ping to {Target} failed");
    public static string Log_GatewayDetected => T(
        "El monitor de red detectó automáticamente el gateway por defecto {Gateway}",
        "Network monitor auto-detected default gateway {Gateway}");
    public static string Log_GatewayDetectFailed => T(
        "Falló la autodetección del gateway por defecto; se usa el respaldo {Fallback}",
        "Failed to auto-detect default gateway; falling back to {Fallback}");
    public static string Log_NoGatewayUsingFallback => T(
        "El monitor de red no pudo detectar un gateway por defecto; usa el host de respaldo {Fallback}",
        "Network monitor could not detect a default gateway; using fallback host {Fallback}");

    // ---- CLI-only output (--list-sensors / --send-summary), not written to the log file ----
    public static string Cli_OpeningHardwareMonitor => T(
        "Abriendo LibreHardwareMonitor (ejecutá esto elevado / como Administrador para resultados precisos)...",
        "Opening LibreHardwareMonitor (run this elevated / as Administrator for accurate results)...");
    public static string Cli_ListSensorsDone => T(
        "Listo. Usá los nombres de sensor de arriba para ajustar Monitoring.Voltage.NominalRails en appsettings.<NOMBRE-MAQUINA>.json si un riel no coincide.",
        "Done. Use the sensor names above to adjust Monitoring.Voltage.NominalRails in appsettings.<MACHINE-NAME>.json if a rail isn't matching.");
    public static string Cli_SummarySentOk => T("Mail de resumen diario enviado.", "Daily summary mail sent.");
    public static string Cli_SummarySentFailed => T(
        "El mail de resumen diario FALLÓ al enviarse (revisá los logs).", "Daily summary mail FAILED to send (check logs).");
    public static string Cli_SummaryFailed(string message) => T($"Falló el resumen diario: {message}", $"Daily summary failed: {message}");
}
