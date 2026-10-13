using System.Net;

namespace NexoDesk.Shared.Security;

/// <summary>Limite básico de tentativas de conexão por endereço remoto, em janela deslizante.</summary>
public sealed class ConnectionRateLimiter
{
    private readonly int _maxAttempts;
    private readonly TimeSpan _window;
    private readonly TimeProvider _time;
    private readonly Dictionary<IPAddress, Queue<DateTimeOffset>> _attempts = new();
    private readonly object _gate = new();

    public ConnectionRateLimiter(int maxAttempts = 10, TimeSpan? window = null, TimeProvider? time = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        _maxAttempts = maxAttempts;
        _window = window ?? TimeSpan.FromMinutes(1);
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Registra a tentativa e diz se ela pode prosseguir.</summary>
    public bool TryAcquire(IPAddress address)
    {
        DateTimeOffset now = _time.GetUtcNow();
        lock (_gate)
        {
            if (!_attempts.TryGetValue(address, out Queue<DateTimeOffset>? queue))
            {
                queue = new Queue<DateTimeOffset>();
                _attempts[address] = queue;
            }

            while (queue.Count > 0 && now - queue.Peek() >= _window)
                queue.Dequeue();

            if (queue.Count >= _maxAttempts)
                return false;

            queue.Enqueue(now);

            // Evita crescimento indefinido do dicionário com endereços antigos.
            if (_attempts.Count > 1024)
            {
                foreach (IPAddress key in _attempts.Where(p => p.Value.Count == 0 || now - p.Value.Last() >= _window).Select(p => p.Key).ToList())
                    _attempts.Remove(key);
            }
            return true;
        }
    }
}
