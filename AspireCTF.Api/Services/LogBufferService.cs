using System.Collections.Concurrent;
using AspireCTF.Api.Models;

namespace AspireCTF.Api.Services;

/// <summary>Keeps a bounded ring buffer of log lines per task for replay on SignalR subscribe.</summary>
public sealed class LogBufferService(int capacity = 2000)
{
    private readonly ConcurrentDictionary<string, CircularBuffer> _buffers = new();

    public LogLineDto Append(string taskId, string stream, string text, string? part = null)
    {
        var buffer = _buffers.GetOrAdd(taskId, _ => new CircularBuffer(capacity));
        return buffer.Add(stream, text, part);
    }

    public IReadOnlyList<LogLineDto> Snapshot(string taskId, long afterSeq = 0)
    {
        return _buffers.TryGetValue(taskId, out var buffer)
            ? buffer.Snapshot(afterSeq)
            : [];
    }

    public void Remove(string taskId) => _buffers.TryRemove(taskId, out _);

    private sealed class CircularBuffer(int capacity)
    {
        private readonly object _lock = new();
        private readonly Queue<LogLineDto> _items = new(capacity);
        private long _seq;

        public LogLineDto Add(string stream, string text, string? part)
        {
            var line = new LogLineDto(Interlocked.Increment(ref _seq), DateTimeOffset.UtcNow.ToString("O"), stream, text, part);
            lock (_lock)
            {
                _items.Enqueue(line);
                while (_items.Count > capacity)
                {
                    _items.Dequeue();
                }
            }

            return line;
        }

        public IReadOnlyList<LogLineDto> Snapshot(long afterSeq)
        {
            lock (_lock)
            {
                return _items.Where(l => l.Seq > afterSeq).ToList();
            }
        }
    }
}
