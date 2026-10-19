using System.Collections.Concurrent;

namespace AspireCTF.Api.Services;

/// <summary>
/// 为嵌套沙箱内的 <c>opencode serve</c> 分配端口。
/// 沙箱容器使用 --network host，共享 API 容器的网络命名空间，
/// 因此并发任务必须各自持有唯一端口，否则会出现地址占用。
/// </summary>
public sealed class SandboxPortLease
{
    private const int MinPort = 20000;
    private const int MaxPort = 30000;
    private readonly ConcurrentDictionary<int, string> _leases = new();

    public int Acquire(string taskId)
    {
        for (var i = 0; i < 2000; i++)
        {
            var port = Random.Shared.Next(MinPort, MaxPort);
            if (_leases.TryAdd(port, taskId))
            {
                return port;
            }
        }

        throw new InvalidOperationException("无法为任务分配 opencode serve 端口");
    }

    public void Release(string taskId)
    {
        foreach (var pair in _leases)
        {
            if (string.Equals(pair.Value, taskId, StringComparison.Ordinal))
            {
                _leases.TryRemove(pair.Key, out _);
            }
        }
    }
}
