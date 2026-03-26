using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using NetworkGuard.Data;
using NetworkGuard.Models;

namespace NetworkGuard.Services;

public interface IDeviceResolverService
{
    Task<(string mac, string deviceName)> ResolveAsync(string ipAddress);
    Task<bool> IsExcludedAsync(string ipAddress);
    Task RefreshArpTableAsync();
    ConcurrentDictionary<string, string> GetArpCache();
}

public partial class DeviceResolverService : IDeviceResolverService
{
    private readonly ILogger<DeviceResolverService> _logger;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IConfiguration _config;
    private readonly ConcurrentDictionary<string, string> _arpCache = new();
    private readonly ConcurrentDictionary<string, string> _dhcpHostnames = new();
    private DateTime _lastArpRefresh = DateTime.MinValue;
    private readonly SemaphoreSlim _arpLock = new(1, 1);
    private static readonly TimeSpan ArpCacheTtl = TimeSpan.FromMinutes(5);
    private static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    // Common paths for dnsmasq leases file
    private static readonly string[] LeasesPaths =
    [
        "/var/lib/misc/dnsmasq.leases",
        "/var/lib/dnsmasq/dnsmasq.leases",
        "/tmp/dnsmasq.leases"
    ];

    public DeviceResolverService(ILogger<DeviceResolverService> logger, IDbContextFactory<AppDbContext> dbFactory, IConfiguration config)
    {
        _logger = logger;
        _dbFactory = dbFactory;
        _config = config;
    }

    public async Task<(string mac, string deviceName)> ResolveAsync(string ipAddress)
    {
        if (DateTime.UtcNow - _lastArpRefresh > ArpCacheTtl)
            await RefreshArpTableAsync();

        _arpCache.TryGetValue(ipAddress, out var mac);
        mac ??= "unknown";

        await using var db = await _dbFactory.CreateDbContextAsync();
        Device? device;

        if (mac != "unknown")
        {
            // Look up DHCP hostname for this MAC
            _dhcpHostnames.TryGetValue(mac, out var dhcpHostname);

            // Look up by MAC address (reliable hardware identifier)
            device = await db.Devices.FirstOrDefaultAsync(d => d.MacAddress == mac);

            if (device == null)
            {
                device = new Device
                {
                    MacAddress = mac,
                    IpAddress = ipAddress,
                    HostName = dhcpHostname,
                    LastSeen = DateTime.UtcNow
                };
                db.Devices.Add(device);
                await db.SaveChangesAsync();
            }
            else
            {
                device.IpAddress = ipAddress;
                device.LastSeen = DateTime.UtcNow;
                // Always update DHCP hostname
                if (dhcpHostname != null)
                    device.HostName = dhcpHostname;
                await db.SaveChangesAsync();
            }
        }
        else
        {
            // MAC unknown — use IP as identifier instead
            device = await db.Devices.FirstOrDefaultAsync(d => d.IpAddress == ipAddress);

            if (device == null)
            {
                device = new Device
                {
                    MacAddress = $"ip-{ipAddress}",
                    IpAddress = ipAddress,
                    LastSeen = DateTime.UtcNow
                };
                db.Devices.Add(device);
                await db.SaveChangesAsync();
            }
            else
            {
                device.LastSeen = DateTime.UtcNow;
                await db.SaveChangesAsync();
            }

            mac = device.MacAddress;
        }

        var deviceName = device.DisplayName;
        return (mac, deviceName);
    }

    public async Task<bool> IsExcludedAsync(string ipAddress)
    {
        if (DateTime.UtcNow - _lastArpRefresh > ArpCacheTtl)
            await RefreshArpTableAsync();

        await using var db = await _dbFactory.CreateDbContextAsync();

        if (_arpCache.TryGetValue(ipAddress, out var mac))
        {
            var device = await db.Devices.FirstOrDefaultAsync(d => d.MacAddress == mac);
            return device?.IsExcluded ?? false;
        }

        // Fallback: check by IP
        var deviceByIp = await db.Devices.FirstOrDefaultAsync(d => d.IpAddress == ipAddress);
        return deviceByIp?.IsExcluded ?? false;
    }

    public ConcurrentDictionary<string, string> GetArpCache() => _arpCache;

    public async Task RefreshArpTableAsync()
    {
        if (!await _arpLock.WaitAsync(0)) return;

        try
        {
            var output = await RunArpCommandAsync();
            var newCache = IsWindows ? ParseWindowsArpOutput(output) : ParseLinuxArpOutput(output);

            _arpCache.Clear();
            foreach (var (ip, mac) in newCache)
                _arpCache[ip] = mac;

            // Read DHCP hostnames from dnsmasq leases
            await RefreshDhcpHostnamesAsync();

            _lastArpRefresh = DateTime.UtcNow;
            _logger.LogDebug("ARP table refreshed: {Count} entries, DHCP hostnames: {Hostnames}",
                _arpCache.Count, _dhcpHostnames.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh ARP table");
        }
        finally
        {
            _arpLock.Release();
        }
    }

    private async Task RefreshDhcpHostnamesAsync()
    {
        // Try to read leases from the dnsmasq container via docker exec, or from a mounted path
        var leasesPath = _config.GetValue<string>("Dhcp:LeasesPath");

        string? leasesContent = null;

        if (!string.IsNullOrEmpty(leasesPath) && File.Exists(leasesPath))
        {
            leasesContent = await File.ReadAllTextAsync(leasesPath);
        }
        else if (!IsWindows)
        {
            // Try reading from the container via docker exec
            try
            {
                leasesContent = await RunCommandAsync("docker", "exec networkguard-dhcp cat /var/lib/misc/dnsmasq.leases");
            }
            catch
            {
                // Try common paths
                foreach (var path in LeasesPaths)
                {
                    if (File.Exists(path))
                    {
                        leasesContent = await File.ReadAllTextAsync(path);
                        break;
                    }
                }
            }
        }

        if (string.IsNullOrEmpty(leasesContent)) return;

        _dhcpHostnames.Clear();
        // Format: "timestamp mac ip hostname client-id"
        foreach (var line in leasesContent.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 4)
            {
                var mac = parts[1].ToLowerInvariant();
                var hostname = parts[3];
                if (hostname != "*" && !string.IsNullOrEmpty(hostname))
                    _dhcpHostnames[mac] = hostname;
            }
        }
    }

    private static async Task<string> RunCommandAsync(string fileName, string arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return output;
    }

    private static async Task<string> RunArpCommandAsync()
    {
        var (fileName, arguments) = IsWindows
            ? ("arp", "-a")
            : ("ip", "neigh show");

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return output;
    }

    private static Dictionary<string, string> ParseWindowsArpOutput(string output)
    {
        var result = new Dictionary<string, string>();
        var regex = WindowsArpRegex();

        foreach (var line in output.Split('\n'))
        {
            var match = regex.Match(line);
            if (match.Success)
            {
                var ip = match.Groups[1].Value;
                var mac = match.Groups[2].Value.ToLowerInvariant();
                var type = match.Groups[3].Value;

                if (type.Equals("dynamic", StringComparison.OrdinalIgnoreCase))
                    result[ip] = mac;
            }
        }

        return result;
    }

    private static Dictionary<string, string> ParseLinuxArpOutput(string output)
    {
        // "ip neigh" format: "192.168.1.40 dev eth0 lladdr 04:f7:78:d1:9b:d7 REACHABLE"
        var result = new Dictionary<string, string>();
        var regex = LinuxArpRegex();

        foreach (var line in output.Split('\n'))
        {
            var match = regex.Match(line);
            if (match.Success)
            {
                var ip = match.Groups[1].Value;
                var mac = match.Groups[2].Value.ToLowerInvariant();
                var state = match.Groups[3].Value;

                // Include REACHABLE, STALE, DELAY entries (skip FAILED, INCOMPLETE)
                if (state is not "FAILED" and not "INCOMPLETE")
                    result[ip] = mac;
            }
        }

        return result;
    }

    // Windows: "  192.168.1.40          04-f7-78-d1-9b-d7     dynamic"
    [GeneratedRegex(@"^\s+(\d+\.\d+\.\d+\.\d+)\s+([0-9a-fA-F-]{17})\s+(\w+)", RegexOptions.Compiled)]
    private static partial Regex WindowsArpRegex();

    // Linux: "192.168.1.40 dev eth0 lladdr 04:f7:78:d1:9b:d7 REACHABLE"
    [GeneratedRegex(@"^(\d+\.\d+\.\d+\.\d+)\s+dev\s+\S+\s+lladdr\s+([0-9a-fA-F:]{17})\s+(\w+)", RegexOptions.Compiled)]
    private static partial Regex LinuxArpRegex();
}
