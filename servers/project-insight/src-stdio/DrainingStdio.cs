using System.Text.Json;

namespace ProjectInsightMCP.Stdio;

/// <summary>
/// Wraps the server's input and output streams so that end of input waits for the answers
/// to requests already read, as the pre-SDK loop did. The SDK's transport stops sending the
/// moment its input ends, so a one-shot <c>printf … | projectinsight-mcp</c> pipeline would
/// otherwise get no responses at all, not even to <c>initialize</c>.
/// </summary>
/// <remarks>
/// Requests are tracked by id from the lines read and crossed off when a response with that
/// id is flushed. A request the client cancels is crossed off too, since the SDK may not
/// answer it. Anything the SDK never answers (e.g. an id the SDK rejects) is given up on
/// once no tool call is running and nothing has happened for <see cref="QuietPeriod"/>.
/// </remarks>
internal sealed class DrainingStdio
{
    private readonly object _lock = new();
    private readonly Dictionary<string, int> _pending = new();
    private readonly Func<bool> _isBusy;
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _lastActivity = Environment.TickCount64;

    public DrainingStdio(Stream input, Stream output, Func<bool> isBusy, TimeSpan? quietPeriod = null)
    {
        _isBusy = isBusy;
        QuietPeriod = quietPeriod ?? TimeSpan.FromSeconds(5);
        Input = new InputStream(input, this);
        Output = new OutputStream(output, this);
    }

    public Stream Input { get; }
    public Stream Output { get; }
    public TimeSpan QuietPeriod { get; }

    internal int PendingCount { get { lock (_lock) return _pending.Values.Sum(); } }

    private void OnLineRead(ReadOnlySpan<byte> line)
    {
        if (!TryReadEnvelope(line, out var id, out var method, out var cancelledId, out _))
            return;

        if (method is not null && id is not null)
            Change(id, +1);
        else if (method == "notifications/cancelled" && cancelledId is not null)
            Change(cancelledId, -1);
    }

    private void OnLineFlushed(ReadOnlySpan<byte> line)
    {
        if (TryReadEnvelope(line, out var id, out var method, out _, out var isResponse)
            && method is null && isResponse && id is not null)
            Change(id, -1);
    }

    private void Change(string id, int delta)
    {
        TaskCompletionSource changed;
        lock (_lock)
        {
            var count = _pending.GetValueOrDefault(id) + delta;
            if (count > 0) _pending[id] = count;
            else _pending.Remove(id);

            Volatile.Write(ref _lastActivity, Environment.TickCount64);
            changed = _changed;
            _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        changed.TrySetResult();
    }

    /// <summary>Waits until every request read so far has been answered (or given up on).</summary>
    private async Task DrainAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task changed;
            lock (_lock)
            {
                if (_pending.Count == 0 && !_isBusy())
                    return;
                changed = _changed.Task;
            }

            if (_isBusy())
                Volatile.Write(ref _lastActivity, Environment.TickCount64);
            else if (Environment.TickCount64 - Volatile.Read(ref _lastActivity) >= QuietPeriod.TotalMilliseconds)
                return;

            await Task.WhenAny(changed, Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken))
                .WaitAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Reads the top level of a JSON-RPC message: its id (as a key, or null if absent or not a
    /// string/integer), its method, a cancelled notification's requestId, and whether it is a response.
    /// </summary>
    internal static bool TryReadEnvelope(ReadOnlySpan<byte> line, out string? id, out string? method,
        out string? cancelledId, out bool isResponse)
    {
        id = method = cancelledId = null;
        isResponse = false;
        try
        {
            var reader = new Utf8JsonReader(line, new JsonReaderOptions { MaxDepth = int.MaxValue });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return false;

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var name = reader.GetString();
                reader.Read();
                switch (name)
                {
                    case "id":
                        id = IdKey(ref reader);
                        reader.Skip();
                        break;
                    case "method" when reader.TokenType == JsonTokenType.String:
                        method = reader.GetString();
                        break;
                    case "result" or "error":
                        isResponse = true;
                        reader.Skip();
                        break;
                    case "params" when reader.TokenType == JsonTokenType.StartObject:
                        var depth = reader.CurrentDepth;
                        while (reader.Read() && reader.CurrentDepth > depth)
                        {
                            if (reader.TokenType != JsonTokenType.PropertyName) continue;
                            var isRequestId = reader.ValueTextEquals("requestId"u8);
                            reader.Read();
                            if (isRequestId && reader.CurrentDepth == depth + 1)
                                cancelledId = IdKey(ref reader);
                            reader.Skip();
                        }
                        break;
                    default:
                        reader.Skip();
                        break;
                }
            }
            return true;
        }
        catch (Exception)
        {
            // Best effort: a line we can't read (invalid JSON, lone surrogates…) just isn't tracked.
            return false;
        }
    }

    // Same id forms the SDK accepts: strings and integers. Prefixed so "1" and 1 stay distinct.
    private static string? IdKey(ref Utf8JsonReader reader) => reader.TokenType switch
    {
        JsonTokenType.String => "s:" + reader.GetString(),
        JsonTokenType.Number when reader.TryGetInt64(out var n) => "n:" + n,
        _ => null
    };

    /// <summary>Splits a byte stream into newline-terminated lines.</summary>
    private sealed class LineSplitter
    {
        private readonly MemoryStream _partial = new();

        public void Append(ReadOnlySpan<byte> data, List<byte[]> completed)
        {
            int newline;
            while ((newline = data.IndexOf((byte)'\n')) >= 0)
            {
                _partial.Write(data[..newline]);
                completed.Add(TakeLine());
                data = data[(newline + 1)..];
            }
            _partial.Write(data);
        }

        public byte[]? TakeRemainder() => _partial.Length > 0 ? TakeLine() : null;

        private byte[] TakeLine()
        {
            var line = _partial.ToArray();
            _partial.SetLength(0);
            return line.Length > 0 && line[^1] == (byte)'\r' ? line[..^1] : line;
        }
    }

    private sealed class InputStream(Stream inner, DrainingStdio owner) : Stream
    {
        private readonly LineSplitter _lines = new();
        private readonly List<byte[]> _completed = new();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            // Console streams ignore cancellation, so wait on the token separately (as the SDK's
            // own stdin wrapper does) to keep host shutdown from hanging on a blocked read.
            var read = await inner.ReadAsync(buffer, cancellationToken).AsTask().WaitAsync(cancellationToken);
            if (read > 0)
            {
                _lines.Append(buffer.Span[..read], _completed);
                foreach (var line in _completed)
                    owner.OnLineRead(line);
                _completed.Clear();
                return read;
            }

            if (_lines.TakeRemainder() is { } last)
                owner.OnLineRead(last);
            await owner.DrainAsync(cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class OutputStream(Stream inner, DrainingStdio owner) : Stream
    {
        private readonly object _lock = new();
        private readonly LineSplitter _lines = new();
        private readonly List<byte[]> _unflushed = new();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await inner.WriteAsync(buffer, cancellationToken);
            Track(buffer.Span);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            Track(buffer.AsSpan(offset, count));
        }

        // A response only counts as answered once it has actually left the process.
        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            await inner.FlushAsync(cancellationToken);
            Flushed();
        }

        public override void Flush()
        {
            inner.Flush();
            Flushed();
        }

        private void Track(ReadOnlySpan<byte> data)
        {
            lock (_lock) _lines.Append(data, _unflushed);
        }

        private void Flushed()
        {
            byte[][] lines;
            lock (_lock)
            {
                lines = _unflushed.ToArray();
                _unflushed.Clear();
            }
            foreach (var line in lines)
                owner.OnLineFlushed(line);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
