using System.Collections.Concurrent;

namespace NetworkGuard.Services;

public interface IDomainCategoryService
{
    string? GetCategory(string domain);
    Task LoadBlocklistsAsync();
    IReadOnlyDictionary<string, int> GetCategoryCounts();
    List<string> GetCategories();
    List<string> GetDomainsForCategory(string category);
    Task AddDomainAsync(string domain, string category);
    Task RemoveDomainAsync(string domain, string category);
    Task CreateCategoryAsync(string category);
    Task DeleteCategoryAsync(string category);
}

public class DomainCategoryService : IDomainCategoryService
{
    private readonly ILogger<DomainCategoryService> _logger;
    private readonly IWebHostEnvironment _env;
    private readonly ConcurrentDictionary<string, string> _domainToCategory = new();
    private readonly ConcurrentDictionary<string, int> _categoryCounts = new();

    public DomainCategoryService(ILogger<DomainCategoryService> logger, IWebHostEnvironment env)
    {
        _logger = logger;
        _env = env;
    }

    // Keywords that flag a domain as adult content when found in the domain name
    private static readonly string[] FlaggedKeywords =
    [
        "porn", "xxx", "sex", "nude", "naked", "hentai", "milf",
        "fetish", "nsfw", "boob", "dildo", "escort", "hookup",
        "camgirl", "livesex", "adultvideo", "xrated"
    ];

    public string? GetCategory(string domain)
    {
        if (string.IsNullOrWhiteSpace(domain)) return null;

        var current = domain.ToLowerInvariant().TrimEnd('.');

        // Check the full domain, then progressively strip subdomains
        while (current.Contains('.'))
        {
            if (_domainToCategory.TryGetValue(current, out var category))
                return category;
            current = current[(current.IndexOf('.') + 1)..];
        }

        // Check the bare TLD+1 (e.g., "example.com" after stripping "www.")
        if (_domainToCategory.TryGetValue(current, out var cat))
            return cat;

        // Keyword matching — flag domains containing suspicious words
        var domainWithoutTld = domain.ToLowerInvariant().TrimEnd('.');
        foreach (var keyword in FlaggedKeywords)
        {
            if (domainWithoutTld.Contains(keyword))
                return "adult";
        }

        return null;
    }

    public async Task LoadBlocklistsAsync()
    {
        var blocklistDir = Path.Combine(_env.ContentRootPath, "blocklists");
        if (!Directory.Exists(blocklistDir))
        {
            _logger.LogWarning("Blocklist directory not found: {Dir}", blocklistDir);
            return;
        }

        _domainToCategory.Clear();
        _categoryCounts.Clear();

        foreach (var file in Directory.GetFiles(blocklistDir, "*.txt"))
        {
            var category = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
            var count = 0;

            var lines = await File.ReadAllLinesAsync(file);
            foreach (var line in lines)
            {
                var trimmed = line.Trim().ToLowerInvariant();

                // Skip comments and blank lines
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
                    continue;

                // Handle hosts file format: "0.0.0.0 domain.com" or "127.0.0.1 domain.com"
                if (trimmed.StartsWith("0.0.0.0") || trimmed.StartsWith("127.0.0.1"))
                {
                    var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2)
                        trimmed = parts[1];
                    else
                        continue;
                }

                // Skip localhost entries
                if (trimmed is "localhost" or "localhost.localdomain" or "local")
                    continue;

                _domainToCategory.TryAdd(trimmed, category);
                count++;
            }

            _categoryCounts[category] = count;
            _logger.LogInformation("Loaded {Count} domains for category '{Category}'", count, category);
        }

        _logger.LogInformation("Total blocklist domains loaded: {Total}", _domainToCategory.Count);
    }

    public IReadOnlyDictionary<string, int> GetCategoryCounts()
    {
        return _categoryCounts.AsReadOnly();
    }

    public List<string> GetCategories()
    {
        return _categoryCounts.Keys.OrderBy(c => c).ToList();
    }

    public List<string> GetDomainsForCategory(string category)
    {
        return _domainToCategory
            .Where(kvp => kvp.Value.Equals(category, StringComparison.OrdinalIgnoreCase))
            .Select(kvp => kvp.Key)
            .OrderBy(d => d)
            .ToList();
    }

    public async Task AddDomainAsync(string domain, string category)
    {
        var normalized = domain.Trim().ToLowerInvariant().TrimEnd('.');
        var cat = category.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(normalized) || string.IsNullOrEmpty(cat)) return;

        _domainToCategory[normalized] = cat;
        _categoryCounts[cat] = _domainToCategory.Count(kvp => kvp.Value == cat);

        await SaveCategoryFileAsync(cat);
    }

    public async Task RemoveDomainAsync(string domain, string category)
    {
        var normalized = domain.Trim().ToLowerInvariant();
        var cat = category.Trim().ToLowerInvariant();

        _domainToCategory.TryRemove(normalized, out _);
        _categoryCounts[cat] = _domainToCategory.Count(kvp => kvp.Value == cat);

        await SaveCategoryFileAsync(cat);
    }

    public async Task CreateCategoryAsync(string category)
    {
        var cat = category.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(cat)) return;

        var blocklistDir = Path.Combine(_env.ContentRootPath, "blocklists");
        var filePath = Path.Combine(blocklistDir, $"{cat}.txt");

        if (!File.Exists(filePath))
        {
            await File.WriteAllTextAsync(filePath, $"# {category} domains\n");
            _categoryCounts[cat] = 0;
        }
    }

    public async Task DeleteCategoryAsync(string category)
    {
        var cat = category.Trim().ToLowerInvariant();

        // Remove all domains in this category from memory
        var domainsToRemove = _domainToCategory
            .Where(kvp => kvp.Value == cat)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var domain in domainsToRemove)
            _domainToCategory.TryRemove(domain, out _);

        _categoryCounts.TryRemove(cat, out _);

        // Delete the file
        var blocklistDir = Path.Combine(_env.ContentRootPath, "blocklists");
        var filePath = Path.Combine(blocklistDir, $"{cat}.txt");
        if (File.Exists(filePath))
            File.Delete(filePath);

        await Task.CompletedTask;
    }

    private async Task SaveCategoryFileAsync(string category)
    {
        var blocklistDir = Path.Combine(_env.ContentRootPath, "blocklists");
        var filePath = Path.Combine(blocklistDir, $"{category}.txt");

        var domains = _domainToCategory
            .Where(kvp => kvp.Value == category)
            .Select(kvp => kvp.Key)
            .OrderBy(d => d);

        var lines = new List<string> { $"# {category} domains" };
        lines.AddRange(domains);

        await File.WriteAllLinesAsync(filePath, lines);
    }
}
