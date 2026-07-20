using LibreHardwareMonitor.Hardware;
using Microsoft.Extensions.Hosting.WindowsServices;
using ResourceAlerter;
using ResourceAlerter.Alerting;
using ResourceAlerter.Configuration;
using ResourceAlerter.Data;
using ResourceAlerter.Localization;
using ResourceAlerter.Logging;
using ResourceAlerter.Monitors;
using ResourceAlerter.Reporting;

if (args.Contains("--list-sensors"))
{
    ListSensors();
    return;
}

// The installer only ever ships/updates appsettings.example.json — it never touches
// appsettings.json itself, so an upgrade or even an uninstall/reinstall can never clobber a
// server's real configuration (this used to be "enforced" via WiX Component Permanent/
// NeverOverwrite flags, which turned out not to be reliable in practice). On a genuinely fresh
// install there's no appsettings.json yet, so bootstrap one from the example here.
var appSettingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
var exampleSettingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.example.json");
if (!File.Exists(appSettingsPath) && File.Exists(exampleSettingsPath))
{
    File.Copy(exampleSettingsPath, appSettingsPath);
}

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

builder.Configuration
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
    .AddJsonFile($"appsettings.{Environment.MachineName}.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables();

builder.Services.AddWindowsService(o => o.ServiceName = "ResourceAlerter");

builder.Services.Configure<MonitoringOptions>(builder.Configuration.GetSection(MonitoringOptions.SectionName));
builder.Services.Configure<SmtpOptions>(builder.Configuration.GetSection(SmtpOptions.SectionName));
builder.Services.Configure<GeneralOptions>(builder.Configuration.GetSection(GeneralOptions.SectionName));
builder.Services.Configure<DatabaseOptions>(builder.Configuration.GetSection(DatabaseOptions.SectionName));
builder.Services.Configure<DiscordOptions>(builder.Configuration.GetSection(DiscordOptions.SectionName));

// Set once, up front, so every mail/Discord alert built anywhere downstream (Worker,
// AlertStateTracker, DailySummaryService, the monitors' DisplayValue/DisplayThreshold text)
// comes out in the right language from the very first cycle. Internal logs are unaffected —
// Strings only covers user-facing text, on purpose.
var generalOptionsForLanguage = new GeneralOptions();
builder.Configuration.GetSection(GeneralOptions.SectionName).Bind(generalOptionsForLanguage);
Strings.CurrentLanguage = generalOptionsForLanguage.Language;

var fileLoggingOptions = new FileLoggingOptions();
builder.Configuration.GetSection(FileLoggingOptions.SectionName).Bind(fileLoggingOptions);
builder.Logging.AddProvider(new FileLoggerProvider(fileLoggingOptions));

builder.Services.AddSingleton<SmtpAlertSender>();
builder.Services.AddSingleton<DiscordAlertSender>();
builder.Services.AddSingleton<IAlertSender, CompositeAlertSender>();

builder.Services.AddSingleton<HardwareMonitorAccessor>();
builder.Services.AddSingleton<DataRecorder>();
builder.Services.AddSingleton<DailySummaryService>();

builder.Services.AddSingleton<IHealthMonitor, CpuMonitor>();
builder.Services.AddSingleton<IHealthMonitor, MemoryMonitor>();
builder.Services.AddSingleton<IHealthMonitor, DiskMonitor>();
builder.Services.AddSingleton<IHealthMonitor, TemperatureMonitor>();
builder.Services.AddSingleton<IHealthMonitor, VoltageMonitor>();
builder.Services.AddSingleton<IHealthMonitor, NetworkMonitor>();

builder.Services.AddHostedService<Worker>();

var isWindowsService = WindowsServiceHelpers.IsWindowsService();
if (!isWindowsService)
{
    builder.Logging.AddConsole();
}

var host = builder.Build();

// Builds and sends the daily summary (last 24h) immediately, then exits — either as a manual
// test hook, or as what the Viewer's "send today's summary" button launches (elevated, so
// HardwareMonitorAccessor gets the same sensor access the service itself has). Exit code
// reflects whether the mail actually made it out, not just whether this process ran cleanly.
if (args.Contains("--send-summary"))
{
    try
    {
        var summary = host.Services.GetRequiredService<DailySummaryService>();
        var sent = await summary.SendAsync(DateTimeOffset.Now, CancellationToken.None);
        Console.WriteLine(sent ? Strings.Cli_SummarySentOk : Strings.Cli_SummarySentFailed);
        Environment.ExitCode = sent ? 0 : 1;
    }
    catch (Exception ex)
    {
        Console.WriteLine(Strings.Cli_SummaryFailed(ex.Message));
        Environment.ExitCode = 1;
    }
    return;
}

host.Run();

static void ListSensors()
{
    Console.WriteLine(Strings.Cli_OpeningHardwareMonitor);
    Console.WriteLine();

    var computer = new Computer { IsCpuEnabled = true, IsMotherboardEnabled = true };
    computer.Open();

    void Walk(IHardware hardware)
    {
        hardware.Update();
        foreach (var sensor in hardware.Sensors)
        {
            if (sensor.SensorType is SensorType.Voltage or SensorType.Temperature)
            {
                Console.WriteLine($"[{hardware.HardwareType}] {hardware.Name} -> {sensor.SensorType} sensor '{sensor.Name}' = {sensor.Value?.ToString("F3") ?? "n/a"}");
            }
        }

        foreach (var sub in hardware.SubHardware)
        {
            Walk(sub);
        }
    }

    foreach (var hw in computer.Hardware)
    {
        Walk(hw);
    }

    computer.Close();
    Console.WriteLine();
    Console.WriteLine(Strings.Cli_ListSensorsDone);
}
