namespace NetworkGuard.Models;

public class Alert
{
    public int Id { get; set; }
    public DateTime Timestamp { get; set; }
    public int DeviceId { get; set; }
    public Device Device { get; set; } = null!;
    public string Domain { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public bool IsRead { get; set; }
}
