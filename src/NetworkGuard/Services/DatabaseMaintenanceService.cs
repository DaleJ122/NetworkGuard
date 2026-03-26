using Microsoft.EntityFrameworkCore;
using NetworkGuard.Data;

namespace NetworkGuard.Services;

public class DatabaseMaintenanceService : BackgroundService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<DatabaseMaintenanceService> _logger;

    public DatabaseMaintenanceService(
        IDbContextFactory<AppDbContext> dbFactory,
        IConfiguration config,
        ILogger<DatabaseMaintenanceService> logger)
    {
        _dbFactory = dbFactory;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));

        do
        {
            try
            {
                var unflaggedHours = _config.GetValue<int>("Database:UnflaggedRetentionHours");
                if (unflaggedHours <= 0) unflaggedHours = 12;

                var flaggedDays = _config.GetValue<int>("Database:FlaggedRetentionDays");
                if (flaggedDays <= 0) flaggedDays = 7;

                var alertDays = _config.GetValue<int>("Database:AlertRetentionDays");
                if (alertDays <= 0) alertDays = 7;

                await using var db = await _dbFactory.CreateDbContextAsync(stoppingToken);

                // Delete unflagged logs older than 12 hours
                var unflaggedCutoff = DateTime.UtcNow.AddHours(-unflaggedHours);
                var deletedUnflagged = await db.DnsQueryLogs
                    .Where(q => !q.IsFlagged && q.Timestamp < unflaggedCutoff)
                    .ExecuteDeleteAsync(stoppingToken);

                // Delete flagged logs older than 7 days
                var flaggedCutoff = DateTime.UtcNow.AddDays(-flaggedDays);
                var deletedFlagged = await db.DnsQueryLogs
                    .Where(q => q.IsFlagged && q.Timestamp < flaggedCutoff)
                    .ExecuteDeleteAsync(stoppingToken);

                // Delete alerts older than 7 days
                var deletedAlerts = await db.Alerts
                    .Where(a => a.Timestamp < flaggedCutoff)
                    .ExecuteDeleteAsync(stoppingToken);

                if (deletedUnflagged > 0 || deletedFlagged > 0 || deletedAlerts > 0)
                {
                    _logger.LogInformation(
                        "Pruned {Unflagged} unflagged logs (>{UnflaggedHours}h), {Flagged} flagged logs (>{FlaggedDays}d), {Alerts} alerts (>{AlertDays}d)",
                        deletedUnflagged, unflaggedHours, deletedFlagged, flaggedDays, deletedAlerts, alertDays);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Database maintenance failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
