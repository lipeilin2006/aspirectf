using AspireCTF.Api.Models;

namespace AspireCTF.Api.Services;

public sealed class ModelCatalogService(
    OpenCodeServerService opencode)
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);
    private readonly object _sync = new();
    private DateTimeOffset _expiresAt;
    private IReadOnlyList<ConfiguredModelDto> _models = [];

    public async Task<IReadOnlyList<ConfiguredModelDto>> GetAsync(CancellationToken ct = default)
    {
        IReadOnlyList<ConfiguredModelDto> models;
        lock (_sync) models = DateTimeOffset.UtcNow < _expiresAt ? _models : [];

        if (models.Count == 0)
        {
            models = (await opencode.GetModelsAsync(ct)).Models;
            lock (_sync)
            {
                _models = models;
                _expiresAt = DateTimeOffset.UtcNow.Add(CacheDuration);
            }
        }

        return models
            .OrderByDescending(x => x.IsFree)
            .ThenBy(x => x.ProviderLabel, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public void Invalidate()
    {
        lock (_sync)
        {
            _models = [];
            _expiresAt = DateTimeOffset.MinValue;
        }
    }

    public void InvalidateAndRefresh() => Invalidate();
}
