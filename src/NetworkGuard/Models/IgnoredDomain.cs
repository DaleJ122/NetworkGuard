namespace NetworkGuard.Models;

public class IgnoredDomain
{
    public int Id { get; set; }
    public string MacAddress { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}
