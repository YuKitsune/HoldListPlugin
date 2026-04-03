using System.Collections.Concurrent;

namespace HoldPlugin;

public class FdrSemaphoreProvider
{
    readonly ConcurrentDictionary<string, SemaphoreSlim> _semaphores = new();

    public SemaphoreSlim Get(string callsign)
    {
        return _semaphores.GetOrAdd(callsign, _ => new SemaphoreSlim(1, 1));
    }
}