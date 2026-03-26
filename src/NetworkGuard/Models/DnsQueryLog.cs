namespace NetworkGuard.Models;

public class DnsQueryLog
{
    public int Id { get; set; }
    public DateTime Timestamp { get; set; }
    public string SourceIp { get; set; } = string.Empty;
    public string MacAddress { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    public string QueryType { get; set; } = string.Empty;
    public string? Category { get; set; }
    public bool IsFlagged { get; set; }
}
