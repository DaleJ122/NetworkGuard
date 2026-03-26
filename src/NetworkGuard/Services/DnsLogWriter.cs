using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using NetworkGuard.Data;
using NetworkGuard.Models;

namespace NetworkGuard.Services;

public interface IDnsLogWriter
{
    ValueTask EnqueueAsync(DnsQueryLog log);
}

public class DnsLogWriter : BackgroundService, IDnsLogWriter
{
    private readonly Channel<DnsQueryLog> _channel = Channel.CreateBounded<DnsQueryLog>(
        new BoundedChannelOptions(10_000)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ILogger<DnsLogWriter> _logger;

    public DnsLogWriter(IDbContextFactory<AppDbContext> dbFactory, ILogger<DnsLogWriter> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public ValueTask EnqueueAsync(DnsQueryLog log)
    {
        return _channel.Writer.WriteAsync(log);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DNS log writer started");
        var batch = new List<DnsQueryLog>();

        while (!stoppingToken.IsCancellationRequested)
        {
            // Wait for the first item
            DnsQueryLog first;
            try
            {
                first = await _channel.Reader.ReadAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            batch.Add(first);

            // Wait a short window to collect A + AAAA pairs
            using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            delayCts.CancelAfter(TimeSpan.FromMilliseconds(500));

            try
            {
                while (batch.Count < 200)
                {
                    var log = await _channel.Reader.ReadAsync(delayCts.Token);
                    batch.Add(log);
                }
            }
            catch (OperationCanceledException)
            {
                // Timer expired or shutdown — drain whatever is left
                while (_channel.Reader.TryRead(out var extra))
                    batch.Add(extra);
            }

            // Merge entries with same device+domain (e.g. A + AAAA queries)
            var merged = MergeBatch(batch);

            try
            {
                await using var db = await _dbFactory.CreateDbContextAsync(stoppingToken);
                db.DnsQueryLogs.AddRange(merged);
                await db.SaveChangesAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to write {Count} DNS log entries", merged.Count);
            }

            batch.Clear();
        }
    }

    private static List<DnsQueryLog> MergeBatch(List<DnsQueryLog> batch)
    {
        var grouped = batch.GroupBy(log => $"{log.SourceIp}:{log.Domain}");
        var merged = new List<DnsQueryLog>();

        foreach (var group in grouped)
        {
            var first = group.First();
            var queryTypes = group.Select(g => g.QueryType).Distinct().OrderBy(t => t);
            first.QueryType = string.Join(", ", queryTypes);
            merged.Add(first);
        }

        return merged;
    }
}
