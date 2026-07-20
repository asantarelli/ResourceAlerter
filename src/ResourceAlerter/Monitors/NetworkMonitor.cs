using System.Net.NetworkInformation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ResourceAlerter.Configuration;
using ResourceAlerter.Localization;

namespace ResourceAlerter.Monitors;

/// <summary>
/// Detects network microcuts by pinging a target host (default: the active interface's default
/// gateway, falling back to a public DNS host) and tracking losses in a moving window plus
/// consecutive-outage duration. Pings run on their own cadence (<see cref="NetworkOptions.PingIntervalSeconds"/>),
/// decoupled from the main polling loop interval: <see cref="Check"/> only issues a new ping
/// once that interval has elapsed and otherwise returns the last computed result.
/// </summary>
public sealed class NetworkMonitor : IHealthMonitor
{
    public string Name => "Network";
    public MonitorOptionsBase Options => _options;

    private readonly NetworkOptions _options;
    private readonly ILogger<NetworkMonitor> _logger;
    private readonly Queue<bool> _window = new();
    private readonly Ping _ping = new();

    private string? _resolvedTarget;
    private DateTimeOffset _lastPingAt = DateTimeOffset.MinValue;
    private DateTimeOffset? _lastSuccessAt;
    private DateTimeOffset? _firstAttemptAt;
    private MonitorResult? _lastResult;
    private bool _currentlyOutOfRange;

    public NetworkMonitor(IOptions<MonitoringOptions> options, ILogger<NetworkMonitor> logger)
    {
        _options = options.Value.Network;
        _logger = logger;
    }

    public IReadOnlyList<MonitorResult> Check()
    {
        var now = DateTimeOffset.UtcNow;
        if (_lastResult is null || now - _lastPingAt >= TimeSpan.FromSeconds(_options.PingIntervalSeconds))
        {
            _lastResult = DoPing(now);
        }

        return new[] { _lastResult! };
    }

    private MonitorResult DoPing(DateTimeOffset now)
    {
        _lastPingAt = now;
        _firstAttemptAt ??= now;

        var target = _resolvedTarget ??= ResolveTarget();
        var success = TryPing(target);

        _window.Enqueue(success);
        while (_window.Count > _options.WindowSize)
        {
            _window.Dequeue();
        }

        if (success)
        {
            _lastSuccessAt = now;
        }

        var losses = _window.Count(x => !x);
        var outageStart = _lastSuccessAt ?? _firstAttemptAt.Value;
        var outageDuration = success ? TimeSpan.Zero : now - outageStart;

        var lossExceeded = losses > _options.MaxLossesInWindow;
        var outageExceeded = outageDuration.TotalSeconds > _options.MaxConsecutiveOutageSeconds;
        _currentlyOutOfRange = lossExceeded || outageExceeded;

        return new MonitorResult
        {
            Subject = target,
            InRange = !_currentlyOutOfRange,
            DisplayValue = Strings.Network_LossesInWindow(losses, _window.Count) +
                            (outageDuration > TimeSpan.Zero ? Strings.Network_Outage(outageDuration.TotalSeconds) : ""),
            NumericValue = losses,
            Unit = $"losses/{_options.WindowSize}",
            DisplayThreshold = Strings.Network_Threshold(_options.MaxLossesInWindow, _options.WindowSize, _options.MaxConsecutiveOutageSeconds),
        };
    }

    private bool TryPing(string target)
    {
        try
        {
            var reply = _ping.Send(target, _options.PingTimeoutMilliseconds);
            return reply?.Status == IPStatus.Success;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, Strings.Log_PingFailed, target);
            return false;
        }
    }

    private string ResolveTarget()
    {
        if (!string.IsNullOrWhiteSpace(_options.TargetHost))
        {
            return _options.TargetHost;
        }

        try
        {
            var gateway = NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up &&
                              nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(nic => nic.GetIPProperties().GatewayAddresses)
                .Select(g => g.Address)
                .FirstOrDefault(addr => addr is not null && addr.ToString() != "0.0.0.0");

            if (gateway is not null)
            {
                _logger.LogInformation(Strings.Log_GatewayDetected, gateway);
                return gateway.ToString();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, Strings.Log_GatewayDetectFailed, _options.FallbackHost);
        }

        _logger.LogInformation(Strings.Log_NoGatewayUsingFallback, _options.FallbackHost);
        return _options.FallbackHost;
    }
}
