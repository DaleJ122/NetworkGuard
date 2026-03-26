using Microsoft.EntityFrameworkCore;
using NetworkGuard.Data;
using NetworkGuard.Models;

namespace NetworkGuard.Services;

public interface IAlertService
{
    Task CreateAlertAsync(int deviceId, string domain, string category);
    Task<List<Alert>> GetRecentAlertsAsync(int count = 50);
    Task<int> GetUnreadCountAsync();
    Task MarkAsReadAsync(int alertId);
    Task MarkAllAsReadAsync();
    event Action? OnAlertCreated;
}

public class AlertService : IAlertService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ILogger<AlertService> _logger;

    public event Action? OnAlertCreated;

    public AlertService(IDbContextFactory<AppDbContext> dbFactory, ILogger<AlertService> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task CreateAlertAsync(int deviceId, string domain, string category)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var alert = new Alert
        {
            Timestamp = DateTime.UtcNow,
            DeviceId = deviceId,
            Domain = domain,
            Category = category,
            IsRead = false
        };

        db.Alerts.Add(alert);
        await db.SaveChangesAsync();

        _logger.LogWarning("ALERT: Device {DeviceId} accessed {Domain} (category: {Category})",
            deviceId, domain, category);

        OnAlertCreated?.Invoke();
    }

    public async Task<List<Alert>> GetRecentAlertsAsync(int count = 50)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.Alerts
            .Include(a => a.Device)
            .OrderByDescending(a => a.Timestamp)
            .Take(count)
            .ToListAsync();
    }

    public async Task<int> GetUnreadCountAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.Alerts.CountAsync(a => !a.IsRead);
    }

    public async Task MarkAsReadAsync(int alertId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await db.Alerts
            .Where(a => a.Id == alertId)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.IsRead, true));
    }

    public async Task MarkAllAsReadAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await db.Alerts
            .Where(a => !a.IsRead)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.IsRead, true));
    }
}
