using System.Net.NetworkInformation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ResourceAlerter.Configuration;
using ResourceAlerter.Localization;

namespace ResourceAlerter.Monitors;

/// <summary>
/// Network health via two independent sources, both on the same cadence
/// (<see cref="NetworkOptions.PingIntervalSeconds"/>, decoupled from the main polling loop —
/// <see cref="Check"/> only runs a new cycle once that interval has elapsed, otherwise returns
/// the last computed results):
/// 1. Pinging a target host (default: the active interface's default gateway, falling back to a
///    public DNS host) — reports packet losses/outage duration (as before) plus round-trip
///    latency (new: the ping reply already carries this, previously discarded).
/// 2. Reading a specific network interface's cumulative error/discard and packet counters
///    (<see cref="NetworkOptions.InterfaceName"/>, manually configured — a server can have
///    several NICs, so there's no safe way to auto-pick "the" one) and reporting the delta since
///    the last poll. Silently skipped (same "unavailable sensor" pattern as a missing voltage
///    rail) if not configured or not found; ping-based subjects keep working regardless, since
///    they don't depend on a specific local NIC.
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
    private IReadOnlyList<MonitorResult>? _lastResult;

    private long? _lastInterfaceErrors;
    private long? _lastInterfacePackets;

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
            _lastResult = DoCycle(now);
        }

        return _lastResult!;
    }

    private List<MonitorResult> DoCycle(DateTimeOffset now)
    {
        _lastPingAt = now;
        _firstAttemptAt ??= now;

        var target = _resolvedTarget ??= ResolveTarget();
        var (success, rttMs) = TryPing(target);

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
        var lossesOut = lossExceeded || outageExceeded;

        var latencyOut = rttMs > _options.LatencyThresholdMilliseconds;

        var results = new List<MonitorResult>(4)
        {
            new()
            {
                Subject = target,
                InRange = !lossesOut,
                DisplayValue = Strings.Network_LossesInWindow(losses, _window.Count) +
                                (outageDuration > TimeSpan.Zero ? Strings.Network_Outage(outageDuration.TotalSeconds) : ""),
                NumericValue = losses,
                Unit = $"losses/{_options.WindowSize}",
                DisplayThreshold = Strings.Network_Threshold(_options.MaxLossesInWindow, _options.WindowSize, _options.MaxConsecutiveOutageSeconds),
            },
            new()
            {
                Subject = Strings.Network_LatencySubjectKey,
                InRange = !latencyOut,
                DisplayValue = Strings.Network_LatencyValue(rttMs),
                NumericValue = rttMs,
                Unit = "ms",
                DisplayThreshold = Strings.Network_LatencyThreshold(_options.LatencyThresholdMilliseconds),
            },
        };

        results.AddRange(CheckInterfaceStats());

        return results;
    }

    /// <summary>
    /// On ping failure, latency is reported as the configured timeout (a real worst-case number)
    /// rather than skipping the reading — a single failed ping already shows up via the losses
    /// subject, and reporting "no data" here would rely on the chart's gap-fill-to-0 logic to
    /// paper over it, which would show as a misleadingly *good* (0ms) latency dip during an
    /// actual outage instead of a spike.
    /// </summary>
    private (bool Success, long RoundtripMilliseconds) TryPing(string target)
    {
        try
        {
            var reply = _ping.Send(target, _options.PingTimeoutMilliseconds);
            return reply?.Status == IPStatus.Success ? (true, reply.RoundtripTime) : (false, _options.PingTimeoutMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, Strings.Log_PingFailed, target);
            return (false, _options.PingTimeoutMilliseconds);
        }
    }

    /// <summary>
    /// Interface error/discard and packet counters are cumulative since the NIC was
    /// initialized, so a single reading is meaningless on its own — this reports the delta since
    /// the previous poll. The very first reading (no prior baseline) reports a delta of 0 rather
    /// than "unavailable": marking it unavailable would make <c>Worker.PruneOrphanedSeries</c>
    /// skip pruning the whole Network monitor on every single service restart (the baseline is
    /// in-memory only, so "first reading" happens every startup) — a 0 this one cycle is
    /// harmless and lets pruning work normally from the very first probe.
    /// </summary>
    private IReadOnlyList<MonitorResult> CheckInterfaceStats()
    {
        if (string.IsNullOrWhiteSpace(_options.InterfaceName))
        {
            return Array.Empty<MonitorResult>();
        }

        var nic = ResolveInterface();
        if (nic is null)
        {
            var reason = Strings.Unavailable_NetworkInterfaceNotFound(_options.InterfaceName);
            return new[]
            {
                Unavailable(Strings.Network_ErrorsSubjectKey, reason),
                Unavailable(Strings.Network_TrafficSubjectKey, reason),
            };
        }

        IPInterfaceStatistics stats;
        try
        {
            stats = nic.GetIPStatistics();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, Strings.Log_InterfaceStatsReadFailed, _options.InterfaceName);
            return new[]
            {
                Unavailable(Strings.Network_ErrorsSubjectKey, Strings.Unavailable_NetworkInterfaceStatsFailed),
                Unavailable(Strings.Network_TrafficSubjectKey, Strings.Unavailable_NetworkInterfaceStatsFailed),
            };
        }

        var errors = SumErrors(stats);
        var packets = SumPackets(stats);

        // Clamp negative: a 32-bit counter wraparound or interface reset would otherwise show as
        // a huge negative "error"/"packet" spike instead of just a harmless 0-this-cycle.
        var errorDelta = _lastInterfaceErrors is null ? 0 : Math.Max(0, errors - _lastInterfaceErrors.Value);
        var packetDelta = _lastInterfacePackets is null ? 0 : Math.Max(0, packets - _lastInterfacePackets.Value);
        _lastInterfaceErrors = errors;
        _lastInterfacePackets = packets;

        var errorsOut = errorDelta > _options.MaxInterfaceErrorsPerInterval;

        return new[]
        {
            new MonitorResult
            {
                Subject = Strings.Network_ErrorsSubjectKey,
                InRange = !errorsOut,
                DisplayValue = Strings.Network_ErrorsValue(errorDelta),
                NumericValue = errorDelta,
                Unit = "errors",
                DisplayThreshold = Strings.Network_ErrorsThreshold(_options.MaxInterfaceErrorsPerInterval),
            },
            new MonitorResult
            {
                // Informational only: traffic volume varies too widely by workload to have a
                // sane universal alert threshold. Always InRange, recorded purely for the chart.
                Subject = Strings.Network_TrafficSubjectKey,
                InRange = true,
                DisplayValue = Strings.Network_TrafficValue(packetDelta),
                NumericValue = packetDelta,
                Unit = "packets",
                DisplayThreshold = Strings.Network_TrafficInformational,
            },
        };
    }

    private static long SumErrors(IPInterfaceStatistics stats) =>
        stats.IncomingPacketsWithErrors + stats.OutgoingPacketsWithErrors +
        stats.IncomingPacketsDiscarded + stats.OutgoingPacketsDiscarded;

    private static long SumPackets(IPInterfaceStatistics stats) =>
        stats.UnicastPacketsReceived + stats.UnicastPacketsSent +
        stats.NonUnicastPacketsReceived + stats.NonUnicastPacketsSent;

    private NetworkInterface? ResolveInterface() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(nic => string.Equals(nic.Name, _options.InterfaceName, StringComparison.OrdinalIgnoreCase));

    private static MonitorResult Unavailable(string subject, string reason) => new()
    {
        Subject = subject,
        InRange = true,
        DisplayValue = Strings.NotAvailable,
        DisplayThreshold = Strings.NotAvailable,
        Unavailable = true,
        UnavailableReason = reason,
    };

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
