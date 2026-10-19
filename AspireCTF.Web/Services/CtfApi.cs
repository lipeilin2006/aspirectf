using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AspireCTF.Contracts;
using AspireCTF.Web.Models;

namespace AspireCTF.Web.Services;

public sealed class CtfApi(IHttpClientFactory factory)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private HttpClient Create() => factory.CreateClient("api");

    public string ApiBaseUrl => Create().BaseAddress?.ToString().TrimEnd('/') ?? "";

    // 反序列化失败必须抛 HttpRequestException：轮询循环只容忍网络类异常，
    // 若抛出 JsonException 会直接杀死自动刷新循环，导致只能手动刷新。
    private static async Task<T?> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Orchestrator API 返回 HTTP {(int)response.StatusCode}");
        }

        var text = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(text)) return default;
        try
        {
            return JsonSerializer.Deserialize<T>(text, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new HttpRequestException("Orchestrator API 响应不是有效 JSON", ex);
        }
    }

    public async Task<List<ChallengeTaskDto>> GetTasksAsync(CancellationToken ct = default)
        => await ReadJsonAsync<List<ChallengeTaskDto>>(await Create().GetAsync("/api/tasks", ct), ct) ?? [];

    public async Task<ChallengeTaskDto?> GetTaskAsync(string id, CancellationToken ct = default)
    {
        var response = await Create().GetAsync($"/api/tasks/{id}", ct);
        return response.IsSuccessStatusCode
            ? await ReadJsonAsync<ChallengeTaskDto>(response, ct)
            : null;
    }

    public async Task<List<LogLineDto>> GetLogsAsync(string id, long afterSeq = 0, CancellationToken ct = default)
        => await ReadJsonAsync<List<LogLineDto>>(await Create().GetAsync($"/api/tasks/{id}/logs?afterSeq={afterSeq}", ct), ct) ?? [];

    public async Task<string?> GetWriteupAsync(string id, CancellationToken ct = default)
    {
        var response = await Create().GetAsync($"/api/tasks/{id}/writeup", ct);
        return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(ct) : null;
    }

    public async Task<SystemInfoDto?> GetSystemInfoAsync(CancellationToken ct = default)
        => await ReadJsonAsync<SystemInfoDto>(await Create().GetAsync("/api/system/info", ct), ct);

    public async Task<PlatformSettingsDto?> GetSettingsAsync(CancellationToken ct = default)
        => await ReadJsonAsync<PlatformSettingsDto>(await Create().GetAsync("/api/settings", ct), ct);

    public async Task<List<ConfiguredModelDto>> GetModelsAsync(CancellationToken ct = default)
        => await ReadJsonAsync<List<ConfiguredModelDto>>(await Create().GetAsync("/api/settings/models", ct), ct) ?? [];

    public async Task<List<ProviderOptionDto>> GetProvidersAsync(CancellationToken ct = default)
        => await ReadJsonAsync<List<ProviderOptionDto>>(await Create().GetAsync("/api/settings/providers", ct), ct) ?? [];

    public async Task<PlatformSettingsDto?> UpdateSettingsAsync(PlatformSettingsDto settings, CancellationToken ct = default)
    {
        var response = await Create().PutAsJsonAsync("/api/settings", settings, JsonOptions, ct);
        return response.IsSuccessStatusCode
            ? await ReadJsonAsync<PlatformSettingsDto>(response, ct)
            : null;
    }

    public async Task<List<ApiKeyStatusDto>> GetApiKeysAsync(CancellationToken ct = default)
        => await ReadJsonAsync<List<ApiKeyStatusDto>>(await Create().GetAsync("/api/settings/api-keys", ct), ct) ?? [];

    public async Task<List<ApiKeyStatusDto>> SetApiKeyAsync(string provider, string apiKey, CancellationToken ct = default)
    {
        var response = await Create().PostAsJsonAsync("/api/settings/api-keys", new { Provider = provider, ApiKey = apiKey }, JsonOptions, ct);
        return response.IsSuccessStatusCode
            ? await ReadJsonAsync<List<ApiKeyStatusDto>>(response, ct) ?? []
            : [];
    }

    public async Task<List<ApiKeyStatusDto>> RemoveApiKeyAsync(string provider, CancellationToken ct = default)
    {
        var response = await Create().DeleteAsync($"/api/settings/api-keys/{Uri.EscapeDataString(provider)}", ct);
        return response.IsSuccessStatusCode
            ? await ReadJsonAsync<List<ApiKeyStatusDto>>(response, ct) ?? []
            : [];
    }

    public async Task<List<ChallengeCatalogEntry>> GetChallengesAsync(CancellationToken ct = default)
        => await ReadJsonAsync<List<ChallengeCatalogEntry>>(await Create().GetAsync("/api/challenges", ct), ct) ?? [];

    public async Task<ChallengeTaskDto?> SubmitAsync(SubmitTaskRequest request, CancellationToken ct = default)
    {
        var response = await Create().PostAsJsonAsync("/api/tasks", request, JsonOptions, ct);
        return response.IsSuccessStatusCode
            ? await ReadJsonAsync<ChallengeTaskDto>(response, ct)
            : null;
    }

    public async Task<ChallengeTaskDto?> SubmitZipAsync(
        string name, string category, string description, string targetUrl, string prompt, string model, string challengeId, string createdBy,
        Stream zipStream, string fileName, CancellationToken ct = default)
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(name), "name");
        content.Add(new StringContent(createdBy), "createdBy");
        content.Add(new StringContent(category), "category");
        content.Add(new StringContent(description), "description");
        content.Add(new StringContent(targetUrl), "targetUrl");
        content.Add(new StringContent(prompt), "prompt");
        content.Add(new StringContent(model), "model");
        content.Add(new StringContent(challengeId), "challengeId");
        content.Add(new StreamContent(zipStream), "source", fileName);

        var response = await Create().PostAsync("/api/tasks/upload", content, ct);
        return response.IsSuccessStatusCode
            ? await ReadJsonAsync<ChallengeTaskDto>(response, ct)
            : null;
    }

    public async Task<bool> CancelAsync(string id, string user, CancellationToken ct = default)
        => (await Create().PostAsync($"/api/tasks/{id}/cancel?user={Uri.EscapeDataString(user)}", null, ct)).IsSuccessStatusCode;

    public async Task<bool> ContinueAsync(string id, string additionalPrompt, string user, CancellationToken ct = default)
        => (await Create().PostAsJsonAsync($"/api/tasks/{id}/continue?user={Uri.EscapeDataString(user)}", new { AdditionalPrompt = additionalPrompt }, JsonOptions, ct)).IsSuccessStatusCode;

    public async Task<bool> DeleteTaskAsync(string id, string user, CancellationToken ct = default)
        => (await Create().DeleteAsync($"/api/tasks/{id}?user={Uri.EscapeDataString(user)}", ct)).IsSuccessStatusCode;
}
