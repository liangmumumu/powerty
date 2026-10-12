using System.Collections.Concurrent;
using System.IO;
using Powerty.Core;

namespace Powerty.App;

/// <summary>Diagnostics log with an asynchronous writer: callers only pay for a queue
/// push, so logging can never stall the UI thread on disk I/O (synchronous appends used
/// to land in the middle of the mouse-release and popup paths, where every millisecond
/// of UI-thread work shows up as cursor stutter).</summary>
internal static class DiagnosticsLog
{
    private const long MaxBytes = 512 * 1024;
    private const int KeepBytes = 256 * 1024;
    private static readonly BlockingCollection<string> Pending = new(new ConcurrentQueue<string>());
    private static readonly Thread Writer;

    static DiagnosticsLog()
    {
        Writer = new Thread(WriterLoop)
        {
            IsBackground = true,
            Name = "Powerty.DiagnosticsWriter"
        };
        Writer.Start();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush(TimeSpan.FromMilliseconds(600));
    }

    public static bool Enabled { get; set; }

    private static string LogPath
    {
        get
        {
            var directory = PortableMode.IsPortable(AppContext.BaseDirectory)
                ? AppContext.BaseDirectory
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Powerty");
            return Path.Combine(directory, "diagnostics.log");
        }
    }

    public static void Write(string category, string message)
    {
        if (!Enabled)
        {
            return;
        }

        try
        {
            Pending.Add($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{category}] {message}");
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
        }
    }

    private static void WriterLoop()
    {
        foreach (var first in Pending.GetConsumingEnumerable())
        {
            var batch = new List<string>(16) { first };
            while (batch.Count < 32 && Pending.TryTake(out var next))
            {
                batch.Add(next);
            }

            AppendBatch(batch);
        }
    }

    private static void AppendBatch(IReadOnlyList<string> lines)
    {
        try
        {
            var path = LogPath;
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            TrimIfNeeded(path);
            File.AppendAllText(path, string.Join(Environment.NewLine, lines) + Environment.NewLine);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
        }
    }

    /// <summary>Best-effort drain used on process exit: the writer thread may not get
    /// another chance to run, so pending lines are appended inline.</summary>
    private static void Flush(TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        var lines = new List<string>(32);
        while (Environment.TickCount64 < deadline && Pending.TryTake(out var line))
        {
            lines.Add(line);
        }

        if (lines.Count > 0)
        {
            AppendBatch(lines);
        }
    }

    private static void TrimIfNeeded(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length <= MaxBytes)
        {
            return;
        }

        var bytes = File.ReadAllBytes(path);
        var start = Math.Max(0, bytes.Length - KeepBytes);
        while (start < bytes.Length && bytes[start] != (byte)'\n')
        {
            start++;
        }

        var tail = start < bytes.Length ? bytes[start..] : bytes;
        File.WriteAllBytes(path, tail);
    }
}
