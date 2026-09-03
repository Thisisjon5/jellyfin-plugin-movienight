using System.Collections.Generic;

namespace Jellyfin.Plugin.MovieNight;

/// <summary>
/// Keeps the FIRST lines of ffmpeg stderr (input probe, stream mapping, filter init, first
/// segment opens - where startup failures actually explain themselves) plus a rolling tail of
/// the most recent lines, for surfacing in failure messages (spec §5.4/§7's "Last failure
/// panel"). Head capture added 2026-08-14 while diagnosing the 25fps Go Live stall: a
/// tail-only buffer showed 40 healthy progress lines and nothing else, which made three
/// separate live failures undiagnosable from their own error messages.
/// </summary>
internal sealed class StderrTailBuffer
{
    private const int MaxHeadLines = 60;
    private const int MaxTailLines = 40;
    private readonly object _bufferLock = new();
    private readonly List<string> _head = new();
    private readonly Queue<string> _tail = new();
    private int _totalLines;

    public void Add(string line)
    {
        lock (_bufferLock)
        {
            _totalLines++;
            if (_head.Count < MaxHeadLines)
            {
                _head.Add(line);
                return;
            }

            _tail.Enqueue(line);
            while (_tail.Count > MaxTailLines)
            {
                _tail.Dequeue();
            }
        }
    }

    public override string ToString()
    {
        lock (_bufferLock)
        {
            if (_tail.Count == 0)
            {
                return string.Join('\n', _head);
            }

            var omitted = _totalLines - _head.Count - _tail.Count;
            var middle = omitted > 0 ? $"\n... [{omitted} lines omitted] ...\n" : "\n";
            return string.Join('\n', _head) + middle + string.Join('\n', _tail);
        }
    }
}
