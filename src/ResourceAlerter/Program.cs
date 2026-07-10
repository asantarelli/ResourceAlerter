using LibreHardwareMonitor.Hardware;
using Microsoft.Extensions.Hosting.WindowsServices;
using ResourceAlerter;
using ResourceAlerter.Alerting;
using ResourceAlerter.Configuration;
using ResourceAlerter.Logging;
using ResourceAlerter.Monitors;

if (args.Contains("--list-sensors"))
{
    ListSensors();
    return;
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

var fileLoggingOptions = new FileLoggingOptions();
builder.Configuration.GetSection(FileLoggingOptions.SectionName).Bind(fileLoggingOptions);
builder.Logging.AddProvider(new FileLoggerProvider(fileLoggingOptions));

builder.Services.AddSingleton<IAlertSender, SmtpAlertSender>();

builder.Services.AddSingleton<HardwareMonitorAccessor>();

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
host.Run();

static void ListSensors()
{
    Console.WriteLine("Opening LibreHardwareMonitor (run this elevated / as Administrator for accurate results)...");
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
    Console.WriteLine("Done. Use the sensor names above to adjust Monitoring.Voltage.NominalRails in appsettings.<MACHINE-NAME>.json if a rail isn't matching.");
}
