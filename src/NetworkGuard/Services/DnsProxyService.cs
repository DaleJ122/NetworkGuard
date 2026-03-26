using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using NetworkGuard.Data;

namespace NetworkGuard.Services;

public interface IDnsProxyService
{
    bool IsRunning { get; }
    long TotalQueries { get; }
    long FlaggedQueries { get; }
    DateTime? StartedAt { get; }
}

public class DnsProxyService : BackgroundService, IDnsProxyService
{
    private readonly IConfiguration _config;
    private readonly IDomainCategoryService _categoryService;
    private readonly IDeviceResolverService _deviceResolver;
    private readonly IAlertService _alertService;
    private readonly IDnsLogWriter _logWriter;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ILogger<DnsProxyService> _logger;

    private UdpClient? _listener;
    private long _totalQueries;
    private long _flaggedQueries;
    private readonly ConcurrentDictionary<string, DateTime> _recentAlerts = new();
    private HashSet<string> _ignoredDomains = new();
    private DateTime _lastIgnoreRefresh = DateTime.MinValue;

    public bool IsRunning { get; private set; }
    public long TotalQueries => Interlocked.Read(ref _totalQueries);
    public long FlaggedQueries => Interlocked.Read(ref _flaggedQueries);
    public DateTime? StartedAt { get; private set; }

    public DnsProxyService(
        IConfiguration config,
        IDomainCategoryService categoryService,
        IDeviceResolverService deviceResolver,
        IAlertService alertService,
        IDnsLogWriter logWriter,
        IDbContextFactory<AppDbContext> dbFactory,
        ILogger<DnsProxyService> logger)
    {
        _config = config;
        _categoryService = categoryService;
        _deviceResolver = deviceResolver;
        _alertService = alertService;
        _logWriter = logWriter;
        _dbFactory = dbFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var listenIp = _config.GetValue<string>("Dns:ListenIp") ?? "0.0.0.0";
        var listenPort = _config.GetValue<int>("Dns:ListenPort");
        if (listenPort == 0) listenPort = 53;
        var upstreamServer = _config.GetValue<string>("Dns:UpstreamServer") ?? "8.8.8.8";
        var upstreamPort = _config.GetValue<int>("Dns:UpstreamPort");
        if (upstreamPort == 0) upstreamPort = 53;

        var endpoint = new IPEndPoint(IPAddress.Parse(listenIp), listenPort);

        try
        {
            _listener = new UdpClient(endpoint);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            _logger.LogError(
                "Port {Port} is already in use. On Windows, the DNS Client service binds to port 53. " +
                "To fix: set registry HKLM\\SYSTEM\\CurrentControlSet\\Services\\Dnscache\\Parameters\\EnableStubResolver " +
                "to 0 (DWORD) and reboot. The dashboard is still accessible but DNS monitoring is disabled.",
                listenPort);
            return;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AccessDenied)
        {
            _logger.LogError(
                "Access denied binding to port {Port}. Run this application as Administrator.", listenPort);
            return;
        }

        IsRunning = true;
        StartedAt = DateTime.UtcNow;
        _logger.LogInformation("DNS proxy listening on {Endpoint}, forwarding to {Upstream}:{Port}",
            endpoint, upstreamServer, upstreamPort);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await _listener.ReceiveAsync(stoppingToken);
                _ = ProcessQueryAsync(result, upstreamServer, upstreamPort, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error receiving DNS query");
            }
        }

        IsRunning = false;
        _listener?.Dispose();
    }

    private async Task ProcessQueryAsync(
        UdpReceiveResult request,
        string upstreamServer,
        int upstreamPort,
        CancellationToken ct)
    {
        try
        {
            Interlocked.Increment(ref _totalQueries);

            // Parse the DNS query
            var query = DnsPacketParser.TryParse(request.Buffer, request.Buffer.Length);
            if (query == null) return;

            // Skip reverse lookups and other non-interesting queries
            if (query.Domain.EndsWith(".in-addr.arpa") || query.Domain.EndsWith(".ip6.arpa"))
            {
                await ForwardAndReplyAsync(request, upstreamServer, upstreamPort, ct);
                return;
            }

            // Check if device is blocked
            var sourceIpForBlock = request.RemoteEndPoint.Address.ToString();
            if (await IsDeviceBlockedAsync(sourceIpForBlock, ct))
            {
                // Return NXDOMAIN — device gets no internet
                await SendNxDomainAsync(request);
                return;
            }

            // Forward to upstream DNS and reply to client
            await ForwardAndReplyAsync(request, upstreamServer, upstreamPort, ct);

            // Normalize domain — strip www. prefix
            var domain = query.Domain;
            if (domain.StartsWith("www."))
                domain = domain[4..];

            // Check if device is excluded
            var sourceIp = request.RemoteEndPoint.Address.ToString();
            if (await _deviceResolver.IsExcludedAsync(sourceIp))
                return;

            // Refresh ignore list cache every 30 seconds
            if ((DateTime.UtcNow - _lastIgnoreRefresh).TotalSeconds > 30)
            {
                await using var ignoreDb = await _dbFactory.CreateDbContextAsync(ct);
                _ignoredDomains = (await ignoreDb.IgnoredDomains
                    .Select(i => $"{i.MacAddress}:{i.Domain}")
                    .ToListAsync(ct))
                    .ToHashSet();
                _lastIgnoreRefresh = DateTime.UtcNow;
            }

            // Check if we've already fully processed this device+domain recently
            var dedupKey = $"{sourceIp}:{domain}";
            var now = DateTime.UtcNow;
            var isRepeat = _recentAlerts.TryGetValue($"log:{dedupKey}", out var lastSeen)
                && (now - lastSeen).TotalSeconds < 5;

            string mac;
            string deviceName;
            string? category;
            bool isFlagged;

            if (!isRepeat)
            {
                // First time seeing this device+domain — do full resolution
                _recentAlerts[$"log:{dedupKey}"] = now;
                (mac, deviceName) = await _deviceResolver.ResolveAsync(sourceIp);
                category = _categoryService.GetCategory(domain);
                isFlagged = category != null;
                if (isFlagged) Interlocked.Increment(ref _flaggedQueries);
            }
            else
            {
                // Repeat query (e.g. AAAA after A) — lightweight lookup for log merging
                (mac, deviceName) = await _deviceResolver.ResolveAsync(sourceIp);
                category = _categoryService.GetCategory(domain);
                isFlagged = category != null;
            }

            // Check if this device+domain is ignored
            if (_ignoredDomains.Contains($"{mac}:{domain}"))
                return;

            // Always enqueue — the log writer merges same device+domain into one row
            var log = new Models.DnsQueryLog
            {
                Timestamp = now,
                SourceIp = sourceIp,
                MacAddress = mac,
                DeviceName = deviceName,
                Domain = domain,
                QueryType = query.QueryType,
                Category = category,
                IsFlagged = isFlagged
            };

            await _logWriter.EnqueueAsync(log);

            // Create alert if flagged (deduplicate: skip if same device+domain within 10 seconds)
            if (isFlagged)
            {
                var alertKey = $"{mac}:{domain}";

                var shouldAlert = !_recentAlerts.TryGetValue(alertKey, out var lastAlert)
                    || (now - lastAlert).TotalSeconds > 10;

                if (shouldAlert)
                {
                    _recentAlerts[alertKey] = now;

                    await using var db = await _dbFactory.CreateDbContextAsync(ct);
                    var device = await db.Devices.FirstOrDefaultAsync(d => d.MacAddress == mac, ct);
                    if (device != null)
                    {
                        await _alertService.CreateAlertAsync(device.Id, query.Domain, category!);
                    }
                }

                // Clean up old entries periodically
                if (_recentAlerts.Count > 1000)
                {
                    var cutoff = now.AddMinutes(-1);
                    foreach (var key in _recentAlerts.Keys)
                    {
                        if (_recentAlerts.TryGetValue(key, out var ts) && ts < cutoff)
                            _recentAlerts.TryRemove(key, out _);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing DNS query");
        }
    }

    private async Task ForwardAndReplyAsync(
        UdpReceiveResult request,
        string upstreamServer,
        int upstreamPort,
        CancellationToken ct)
    {
        using var forwarder = new UdpClient();
        var upstreamEp = new IPEndPoint(IPAddress.Parse(upstreamServer), upstreamPort);

        await forwarder.SendAsync(request.Buffer, request.Buffer.Length, upstreamEp);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));

        try
        {
            var response = await forwarder.ReceiveAsync(cts.Token);
            await _listener!.SendAsync(response.Buffer, response.Buffer.Length, request.RemoteEndPoint);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Upstream DNS timeout for query from {Source}", request.RemoteEndPoint);
        }
    }

    private async Task<bool> IsDeviceBlockedAsync(string sourceIp, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // Find device by IP or MAC
        var arpCache = _deviceResolver.GetArpCache();
        arpCache.TryGetValue(sourceIp, out var mac);

        Models.Device? device = null;
        if (mac != null)
            device = await db.Devices.FirstOrDefaultAsync(d => d.MacAddress == mac, ct);
        device ??= await db.Devices.FirstOrDefaultAsync(d => d.IpAddress == sourceIp, ct);

        if (device == null || !device.IsBlocked) return false;

        // Check if block has expired
        if (device.BlockedUntil.HasValue && device.BlockedUntil <= DateTime.UtcNow)
        {
            device.IsBlocked = false;
            device.BlockedUntil = null;
            await db.SaveChangesAsync(ct);
            return false;
        }

        return true;
    }

    private async Task SendNxDomainAsync(UdpReceiveResult request)
    {
        // Build NXDOMAIN response from the query
        var response = new byte[request.Buffer.Length];
        Array.Copy(request.Buffer, response, request.Buffer.Length);

        // Set response flags: QR=1 (response), RCODE=3 (NXDOMAIN)
        response[2] = (byte)((request.Buffer[2] | 0x80) & 0xFB); // QR=1, RD preserved, RA=0
        response[3] = (byte)((request.Buffer[3] & 0xF0) | 0x03); // RCODE=3 (NXDOMAIN)

        // Zero out answer/authority/additional counts
        response[6] = 0; response[7] = 0; // ANCOUNT
        response[8] = 0; response[9] = 0; // NSCOUNT
        response[10] = 0; response[11] = 0; // ARCOUNT

        await _listener!.SendAsync(response, response.Length, request.RemoteEndPoint);
    }
}
