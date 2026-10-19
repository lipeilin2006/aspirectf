using AspireCTF.Web.Models;
using Microsoft.AspNetCore.SignalR.Client;

namespace AspireCTF.Web.Services;

/// <summary>每个 Blazor 电路一个实例，维护到 Orchestrator /hubs/tasks 的 SignalR 连接。</summary>
public sealed class TaskRealtime : IAsyncDisposable
{
    private readonly IHttpClientFactory _factory;
    private readonly ILogger<TaskRealtime> _logger;
    private HubConnection? _connection;

    public TaskRealtime(IHttpClientFactory factory, ILogger<TaskRealtime> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public event Action<ChallengeTaskDto>? TaskUpdated;
    public event Action<string, LogLineDto>? Log;

    public async Task EnsureConnectedAsync(CancellationToken ct = default)
    {
        if (_connection is not null)
        {
            return;
        }

        var baseUrl = _factory.CreateClient("api").BaseAddress ?? new Uri("http://apiservice");
        var hubUri = new Uri(baseUrl, "/hubs/tasks");

        _connection = new HubConnectionBuilder()
            .WithUrl(hubUri)
            .WithAutomaticReconnect([TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)])
            .Build();

        _connection.On<ChallengeTaskDto>("TaskUpdated", dto => TaskUpdated?.Invoke(dto));
        _connection.On<string, LogLineDto>("Log", (taskId, line) => Log?.Invoke(taskId, line));

        _connection.Reconnecting += error =>
        {
            _logger.LogWarning(error, "SignalR 重连中...");
            return Task.CompletedTask;
        };
        _connection.Reconnected += id =>
        {
            _logger.LogInformation("SignalR 已重连 {Id}", id);
            return Task.CompletedTask;
        };
        _connection.Closed += error =>
        {
            _logger.LogWarning(error, "SignalR 连接关闭");
            return Task.CompletedTask;
        };

        await _connection.StartAsync(ct);
    }

    public async Task SubscribeAsync(string taskId, CancellationToken ct = default)
    {
        await EnsureConnectedAsync(ct);
        await _connection!.InvokeAsync("Subscribe", taskId, ct);
    }

    public async Task UnsubscribeAsync(string taskId)
    {
        if (_connection is not null)
        {
            try
            {
                await _connection.InvokeAsync("Unsubscribe", taskId);
            }
            catch
            {
                // connection may already be gone
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }
    }
}
