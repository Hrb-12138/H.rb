using System.Threading;

namespace CnInstantTranslator.Core;

public sealed class IdleOrSpaceTrigger : IDisposable
{
    private static readonly HashSet<char> ChineseBoundaryMarks = new()
    {
        '。', '！', '？', '；'
    };

    private readonly int _idleDelayMs;
    private readonly System.Threading.Timer _timer;
    private long _lastActivityTicks = DateTime.UtcNow.Ticks;
    private int _firedForCurrentBurst;

    public IdleOrSpaceTrigger(int idleDelayMs)
    {
        _idleDelayMs = idleDelayMs;
        _timer = new System.Threading.Timer(_ => TimerCallback(), null, Timeout.Infinite, Timeout.Infinite);
        _timer.Change(150, 150);
    }

    public event Action? IdleElapsed;
    public event Action? BoundaryKeyPressed;

    public bool IsIdle => IdleElapsedMs >= _idleDelayMs;

    public long IdleElapsedMs => (DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastActivityTicks)) / TimeSpan.TicksPerMillisecond;

    public void NotifyActivity()
    {
        Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);
        Interlocked.Exchange(ref _firedForCurrentBurst, 0);
    }

    public void NotifyKey(KeyEvent key)
    {
        NotifyActivity();

        if (key.IsKeyDown && IsBoundaryKey(key))
        {
            BoundaryKeyPressed?.Invoke();
        }
    }

    public bool IsBoundaryKey(KeyEvent key)
    {
        if (key.VirtualKey is Native.NativeMethods.VkSpace or Native.NativeMethods.VkReturn)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(key.Text))
        {
            return false;
        }

        return key.Text.Any(c => ChineseBoundaryMarks.Contains(c) || c is '.' or '!' or '?' or ';');
    }

    private void TimerCallback()
    {
        if (!IsIdle || Interlocked.Exchange(ref _firedForCurrentBurst, 1) == 1)
        {
            return;
        }

        IdleElapsed?.Invoke();
    }

    public void Dispose()
    {
        _timer.Dispose();
    }
}
