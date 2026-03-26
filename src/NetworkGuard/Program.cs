using Microsoft.EntityFrameworkCore;
using NetworkGuard.Components;
using NetworkGuard.Data;
using NetworkGuard.Services;

var builder = WebApplication.CreateBuilder(args);

// Blazor Server
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Database
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? "Data Source=networkguard.db";
builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseSqlite(connectionString));

// Services (singletons for shared state across background services and UI)
builder.Services.AddSingleton<IDomainCategoryService, DomainCategoryService>();
builder.Services.AddSingleton<IDeviceResolverService, DeviceResolverService>();
builder.Services.AddSingleton<IAlertService, AlertService>();

// DnsLogWriter is both a BackgroundService and an injectable service
builder.Services.AddSingleton<DnsLogWriter>();
builder.Services.AddSingleton<IDnsLogWriter>(sp => sp.GetRequiredService<DnsLogWriter>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<DnsLogWriter>());

// DnsProxyService is both a BackgroundService and an injectable service
builder.Services.AddSingleton<DnsProxyService>();
builder.Services.AddSingleton<IDnsProxyService>(sp => sp.GetRequiredService<DnsProxyService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<DnsProxyService>());

// Database maintenance
builder.Services.AddHostedService<DatabaseMaintenanceService>();

builder.Services.AddHttpClient();

var app = builder.Build();

// Initialize database
using (var scope = app.Services.CreateScope())
{
    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
    await using var db = await dbFactory.CreateDbContextAsync();
    await db.Database.EnsureCreatedAsync();

    // Add HostName column if missing (schema migration for existing databases)
    try
    {
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE Devices ADD COLUMN HostName TEXT NULL");
    }
    catch { /* Column already exists */ }

    // Add blocking columns if missing
    try { await db.Database.ExecuteSqlRawAsync("ALTER TABLE Devices ADD COLUMN IsBlocked INTEGER NOT NULL DEFAULT 0"); } catch { }
    try { await db.Database.ExecuteSqlRawAsync("ALTER TABLE Devices ADD COLUMN BlockedUntil TEXT NULL"); } catch { }

    // Create IgnoredDomains table if missing
    try
    {
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS IgnoredDomains (
                Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                MacAddress TEXT NOT NULL,
                Domain TEXT NOT NULL,
                CreatedAt TEXT NOT NULL
            )
            """);
        await db.Database.ExecuteSqlRawAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_IgnoredDomains_MacAddress_Domain ON IgnoredDomains (MacAddress, Domain)");
    }
    catch { /* Already exists */ }

    // Fix empty-string FriendlyNames to NULL
    await db.Database.ExecuteSqlRawAsync(
        "UPDATE Devices SET FriendlyName = NULL WHERE FriendlyName = ''");

    // Clean up ip- prefixed device entries (will be recreated with proper MACs)
    await db.Database.ExecuteSqlRawAsync(
        "DELETE FROM Devices WHERE MacAddress LIKE 'ip-%'");

    // Populate HostName from DHCP leases file for all existing devices
    var leasesPath = builder.Configuration.GetValue<string>("Dhcp:LeasesPath");
    if (!string.IsNullOrEmpty(leasesPath) && File.Exists(leasesPath))
    {
        var lines = await File.ReadAllLinesAsync(leasesPath);
        foreach (var line in lines)
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 4)
            {
                var mac = parts[1].ToLowerInvariant();
                var hostname = parts[3];
                if (hostname != "*")
                {
                    await db.Database.ExecuteSqlRawAsync(
                        "UPDATE Devices SET HostName = {0} WHERE MacAddress = {1} AND (HostName IS NULL OR HostName = '')",
                        hostname, mac);
                }
            }
        }
    }
}

// Merge duplicate log entries (same device+domain within same minute)
using (var scope = app.Services.CreateScope())
{
    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
    await using var db = await dbFactory.CreateDbContextAsync();

    // Set query type to "A, AAAA" on rows we're keeping that have duplicates
    await db.Database.ExecuteSqlRawAsync("""
        UPDATE DnsQueryLogs SET QueryType = 'A, AAAA'
        WHERE Id IN (
            SELECT MIN(Id) FROM DnsQueryLogs
            GROUP BY SourceIp, Domain, SUBSTR(Timestamp, 1, 16)
            HAVING COUNT(*) > 1
        )
        """);

    // Delete duplicate rows, keep the lowest Id per group
    var deleted = await db.Database.ExecuteSqlRawAsync("""
        DELETE FROM DnsQueryLogs
        WHERE Id NOT IN (
            SELECT MIN(Id) FROM DnsQueryLogs
            GROUP BY SourceIp, Domain, SUBSTR(Timestamp, 1, 16)
        )
        """);

    if (deleted > 0)
        app.Logger.LogInformation("Merged and removed {Count} duplicate log entries", deleted);
}

// Load blocklists
var categoryService = app.Services.GetRequiredService<IDomainCategoryService>();
await categoryService.LoadBlocklistsAsync();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStaticFiles();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
