using AspireCTF.Contracts;

namespace AspireCTF.Api.Endpoints;

public static class CatalogEndpoints
{
    public static RouteGroupBuilder MapCatalogEndpoints(this RouteGroupBuilder api, IConfiguration configuration)
    {
        api.MapGet("/challenges", () =>
        {
            var entries = configuration.GetSection("Catalog:Challenges").Get<ChallengeCatalogEntry[]>() ?? [];
            return Results.Ok(entries);
        });

        return api;
    }
}
