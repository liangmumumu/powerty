using System.Diagnostics;
using System.Windows.Threading;

namespace Qwaacty.App;

/// <summary>Background heartbeat that measures how long the UI thread takes to answer a
/// highest-priority dispatch request. Every answer delayed by at least 40 ms is written
/// to the diagnostics log, so "mouse stutter" reports can be correlated with concrete
/// UI-thread stall windows (the low-level input hooks run on the UI thread, so any busy
/// stretch there delays the entire mouse pipeline).</summary>
internal sealed class UiStallProbe
{
    private const int StallThresholdMs = 40;
    private const int IdleGapMs = 15;

    private readonly Dispatcher _dispatcher;
    private readonly Thread _thread;
    private volatile bool _stopped;

    public UiStallProbe(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "Qwaacty.UiStallProbe",
            Priority = ThreadPriority.AboveNormal
        };
    }

    public void Start() => _thread.Start();

    public void Stop() => _stopped = true;

    private void Loop()
    {
        var stopwatch = new Stopwatch();
        while (!_stopped)
        {
            var gen0Before = GC.CollectionCount(0);
            var gen2Before = GC.CollectionCount(2);
            var pauseBefore = GC.GetTotalPauseDuration();
            stopwatch.Restart();
            try
            {
                _dispatcher.Invoke(static () => { }, DispatcherPriority.Send);
            }
            catch (Exception exception) when (exception is InvalidOperationException or TaskCanceledException)
            {
                // The dispatcher is shutting down; nothing left to measure.
                return;
            }

            stopwatch.Stop();
            var elapsedMs = stopwatch.Elapsed.TotalMilliseconds;
            if (elapsedMs >= StallThresholdMs)
            {
                var gcPauseMs = (GC.GetTotalPauseDuration() - pauseBefore).TotalMilliseconds;
                DiagnosticsLog.Write("STALL",
                    $"ui busy {elapsedMs:F0} ms (gc gen0+{GC.CollectionCount(0) - gen0Before} gen2+{GC.CollectionCount(2) - gen2Before} pause {gcPauseMs:F0} ms)");
            }

            Thread.Sleep(IdleGapMs);
        }
    }
}
