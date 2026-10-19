using AspireCTF.Api.Services;
using AspireCTF.Contracts;

namespace AspireCTF.Api.Endpoints;

public static class SettingsEndpoints
{
    public static RouteGroupBuilder MapSettingsEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/settings", (PlatformSettingsService settings) =>
            Results.Ok(settings.ToDto(settings.Current)));

        api.MapGet("/settings/providers", async (ModelsDevCatalogService catalog, CancellationToken ct) =>
            Results.Ok(await catalog.GetProvidersAsync(ct)));

        api.MapGet("/settings/api-keys", async (PlatformSettingsService settings, CancellationToken ct) =>
            Results.Ok(await settings.GetApiKeysAsync(ct)));

        api.MapPost("/settings/api-keys", async (
            ApiKeyUpdateRequest request,
            PlatformSettingsService settings,
            ModelCatalogService models,
            CancellationToken ct) =>
        {
            var saved = await settings.SetApiKeyAsync(request.Provider, request.ApiKey, ct);
            if (!saved) return Results.BadRequest("Provider 或 API Key 无效");
            models.Invalidate();
            return Results.Ok(await settings.GetApiKeysAsync(ct));
        });

        api.MapDelete("/settings/api-keys/{provider}", async (
            string provider,
            PlatformSettingsService settings,
            ModelCatalogService models,
            CancellationToken ct) =>
        {
            var removed = await settings.RemoveApiKeyAsync(provider, ct);
            if (!removed) return Results.NotFound();
            models.Invalidate();
            return Results.Ok(await settings.GetApiKeysAsync(ct));
        });

        api.MapGet("/settings/models", async (ModelCatalogService models, CancellationToken ct) =>
            Results.Ok(await models.GetAsync(ct)));

        api.MapGet("/settings/opencode/validate", async (OpenCodeServerService opencode, CancellationToken ct) =>
            Results.Ok(await opencode.ValidateAsync(ct)));

        api.MapGet("/settings/opencode/cli-models", async (OpenCodeServerService opencode, CancellationToken ct) =>
            Results.Ok(await opencode.GetModelsAsync(ct)));

        api.MapPut("/settings", async (
            PlatformSettingsDto dto,
            PlatformSettingsService settings,
            CancellationToken ct) => Results.Ok(settings.ToDto(await settings.UpdateAsync(dto, ct))));

        return api;
    }
}
