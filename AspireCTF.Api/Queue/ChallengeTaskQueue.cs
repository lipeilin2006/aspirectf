using System.Threading.Channels;
using Microsoft.Extensions.Options;
using AspireCTF.Api.Sandbox;

namespace AspireCTF.Api.Queue;

/// <summary>Unbounded channel queue with a soft capacity limit for pending challenge tasks.</summary>
public sealed class ChallengeTaskQueue
{
    private readonly Channel<string> _channel = Channel.CreateUnbounded<string>();
    private readonly int _maxQueueLength;
    private int _count;

    public ChallengeTaskQueue(IOptions<SandboxOptions> options)
    {
        _maxQueueLength = Math.Max(1, options.Value.MaxQueueLength);
    }

    public int Length => Volatile.Read(ref _count);

    public bool TryEnqueue(string taskId)
    {
        if (Interlocked.Increment(ref _count) > _maxQueueLength)
        {
            Interlocked.Decrement(ref _count);
            return false;
        }

        return _channel.Writer.TryWrite(taskId);
    }

    public async Task<string> DequeueAsync(CancellationToken ct)
    {
        var id = await _channel.Reader.ReadAsync(ct);
        Interlocked.Decrement(ref _count);
        return id;
    }
}
