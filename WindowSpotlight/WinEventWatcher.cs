namespace WindowSpotlight;

internal sealed class WinEventWatcher : IDisposable
{
    private readonly NativeMethods.WinEventProc _callback;
    private readonly List<nint> _hooks = [];
    private bool _disposed;

    public WinEventWatcher(uint targetProcessId)
    {
        _callback = HandleEvent;
        AddHook(NativeMethods.EventSystemForeground, NativeMethods.EventSystemForeground, 0,
            NativeMethods.WineventOutOfContext);
        AddHook(NativeMethods.EventObjectDestroy, NativeMethods.EventObjectDestroy, targetProcessId,
            NativeMethods.WineventOutOfContext);
    }

    public event EventHandler<WinEventArgs>? EventReceived;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var hook in _hooks)
        {
            NativeMethods.UnhookWinEvent(hook);
        }

        _hooks.Clear();
        GC.SuppressFinalize(this);
    }

    private void AddHook(uint minimum, uint maximum, uint processId, uint flags)
    {
        var hook = NativeMethods.SetWinEventHook(minimum, maximum, 0, _callback, processId, 0, flags);
        if (hook != 0)
        {
            _hooks.Add(hook);
        }
    }

    private void HandleEvent(
        nint hook,
        uint eventType,
        nint window,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime)
    {
        EventReceived?.Invoke(this, new WinEventArgs(eventType, window, objectId));
    }
}

internal sealed record WinEventArgs(uint EventType, nint Window, int ObjectId);

internal static class ForegroundClassifier
{
    public static bool IsTargetOrOwned(nint foreground, nint target, Func<nint, nint> ownerResolver)
    {
        if (foreground == 0 || target == 0)
        {
            return false;
        }

        var current = foreground;
        var visited = new HashSet<nint>();
        while (current != 0 && visited.Add(current))
        {
            if (current == target)
            {
                return true;
            }

            current = ownerResolver(current);
        }

        return false;
    }
}
