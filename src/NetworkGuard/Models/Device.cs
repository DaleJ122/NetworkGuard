namespace NetworkGuard.Models;

public class Device
{
    public int Id { get; set; }
    public string MacAddress { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public string? HostName { get; set; }
    public string? FriendlyName { get; set; }
    public bool IsExcluded { get; set; }
    public bool IsBlocked { get; set; }
    public DateTime? BlockedUntil { get; set; }
    public DateTime LastSeen { get; set; }

    public string DisplayName => !string.IsNullOrWhiteSpace(FriendlyName)
        ? FriendlyName
        : !string.IsNullOrWhiteSpace(HostName)
            ? HostName
            : MacAddress;

    public bool IsCurrentlyBlocked => IsBlocked && (BlockedUntil == null || BlockedUntil > DateTime.UtcNow);
}
