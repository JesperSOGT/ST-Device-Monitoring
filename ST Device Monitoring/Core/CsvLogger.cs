using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Channels;
using ST_Device_Monitoring.Models;

namespace ST_Device_Monitoring.Core;

/// <summary>Type of entry written to the log.</summary>
public enum PingEvent
{
    /// <summary>Successful ping (only written when "log all pings" is enabled).</summary>
    Success,
    /// <summary>Failed ping.</summary>
    Failure,
    /// <summary>Last failure written before logging is suppressed for this outage.</summary>
    SuppressionStarted,
    /// <summary>Device answered again after an outage.</summary>
    Recovered,
    /// <summary>Checking paused because the group master is down.</summary>
    Paused,
    /// <summary>Checking resumed because the group master answers again.</summary>
    Resumed
}

public readonly struct LogRecord
{
    public DateTime Timestamp { get; init; }
    public string DeviceName { get; init; }
    public string Host { get; init; }
    public PingEvent Event { get; init; }
    public long RoundtripMs { get; init; }
    public string Status { get; init; }
    public string? Info { get; init; }
    public long ConsecutiveFails { get; init; }

    /// <summary>
    /// Log this entry even when only failures are being logged - used for the first successful
    /// check of a device, so the log always shows when it was first seen online.
    /// </summary>
    public bool Force { get; init; }

    public bool IsSuccess => Event == PingEvent.Success;
}

/// <summary>
/// Thread-safe CSV logger. All ping threads push into a channel (lock-free) and a single
/// background thread writes to disk with a large buffer and a periodic flush.
/// Two files are written per day:
///   ping_yyyyMMdd.csv    - every ping including response time (can be turned off)
///   errors_yyyyMMdd.csv  - failures, suppression notices and recoveries only
/// </summary>
public sealed class CsvLogger : IAsyncDisposable
{
    private const int ChannelCapacity = 200_000;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);

    private readonly Channel<LogRecord> _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;
    private readonly string _separator;

    private StreamWriter? _allWriter;
    private StreamWriter? _errorWriter;
    private string _allPath = string.Empty;
    private string _errorPath = string.Empty;
    private DateTime _currentFileDate = DateTime.MinValue;

    private long _dropped;
    private long _writtenAll;
    private long _writtenErrors;
    private long _rotations;

    public string Directory { get; }
    public bool LogAllPings { get; set; }

    /// <summary>What happens when a file reaches <see cref="MaxFileSizeBytes"/>.</summary>
    public LogRotationMode Rotation { get; set; } = LogRotationMode.None;

    /// <summary>Size limit per file in bytes. 0 = no limit.</summary>
    public long MaxFileSizeBytes { get; set; }

    /// <summary>Ring buffer: percentage of the oldest lines dropped when trimming (10-90).</summary>
    public int RingTrimPercent { get; set; } = 30;

    /// <summary>Number of times a file has been rotated or trimmed.</summary>
    public long Rotations => Interlocked.Read(ref _rotations);
    public long DroppedRecords => Interlocked.Read(ref _dropped);
    public long WrittenAll => Interlocked.Read(ref _writtenAll);
    public long WrittenErrors => Interlocked.Read(ref _writtenErrors);
    public string? LastWriteError { get; private set; }
    public string CurrentAllLogPath => Path.Combine(Directory, $"ping_{DateTime.Now:yyyyMMdd}.csv");
    public string CurrentErrorLogPath => Path.Combine(Directory, $"errors_{DateTime.Now:yyyyMMdd}.csv");

    public CsvLogger(string directory, bool logAllPings, string separator = ";")
    {
        Directory = directory;
        LogAllPings = logAllPings;
        _separator = string.IsNullOrEmpty(separator) ? ";" : separator;

        System.IO.Directory.CreateDirectory(directory);

        _channel = Channel.CreateBounded<LogRecord>(new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        });

        _worker = Task.Run(() => WriteLoopAsync(_cts.Token));
    }

    /// <summary>Called from the ping threads. Never blocks.</summary>
    public void Log(in LogRecord record)
    {
        // Successful checks are only logged when "log every check" is on - except the first
        // successful check of a device, which is always logged.
        // Failures, suppression notices and recoveries are always logged.
        if (record.IsSuccess && !LogAllPings && !record.Force) return;

        if (!_channel.Writer.TryWrite(record))
            Interlocked.Increment(ref _dropped);
    }

    private async Task WriteLoopAsync(CancellationToken ct)
    {
        var reader = _channel.Reader;
        var sw = Stopwatch.StartNew();
        var sb = new StringBuilder(256);

        try
        {
            while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                var sinceCheck = 0;
                while (reader.TryRead(out var rec))
                {
                    WriteRecord(sb, rec);

                    // Also check the size mid-burst so a fast burst cannot overshoot the limit
                    // by much before the next flush.
                    if (++sinceCheck >= 2000)
                    {
                        sinceCheck = 0;
                        Flush();
                        EnforceSizeLimit();
                    }
                }

                if (sw.Elapsed >= FlushInterval)
                {
                    Flush();
                    EnforceSizeLimit();
                    sw.Restart();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        finally
        {
            while (reader.TryRead(out var rec))
                WriteRecord(sb, rec);

            Flush();
            _allWriter?.Dispose();
            _errorWriter?.Dispose();
            _allWriter = null;
            _errorWriter = null;
        }
    }

    private void WriteRecord(StringBuilder sb, in LogRecord rec)
    {
        try
        {
            EnsureWriters(rec.Timestamp);
            var line = Format(sb, rec);

            if (LogAllPings || !rec.IsSuccess || rec.Force)
            {
                _allWriter!.WriteLine(line);
                Interlocked.Increment(ref _writtenAll);
            }
            if (!rec.IsSuccess)
            {
                _errorWriter!.WriteLine(line);
                Interlocked.Increment(ref _writtenErrors);
            }
        }
        catch (Exception ex)
        {
            LastWriteError = ex.Message;
        }
    }

    private void Flush()
    {
        try { _allWriter?.Flush(); } catch (Exception ex) { LastWriteError = ex.Message; }
        try { _errorWriter?.Flush(); } catch (Exception ex) { LastWriteError = ex.Message; }
    }

    private void EnsureWriters(DateTime timestamp)
    {
        var date = timestamp.Date;
        if (_allWriter != null && _errorWriter != null && date == _currentFileDate)
            return;

        Flush();
        _allWriter?.Dispose();
        _errorWriter?.Dispose();

        System.IO.Directory.CreateDirectory(Directory);
        _allPath = Path.Combine(Directory, $"ping_{date:yyyyMMdd}.csv");
        _errorPath = Path.Combine(Directory, $"errors_{date:yyyyMMdd}.csv");
        _allWriter = OpenFile(_allPath);
        _errorWriter = OpenFile(_errorPath);
        _currentFileDate = date;
    }

    // ---------- Size limit: rotate + zip, or ring buffer ----------

    /// <summary>
    /// Runs on the writer thread after each flush. Either starts a new file and compresses the
    /// old one, or trims the oldest lines out of the current file - depending on the mode.
    /// </summary>
    private void EnforceSizeLimit()
    {
        if (Rotation == LogRotationMode.None || MaxFileSizeBytes <= 0) return;

        try
        {
            if (_allWriter != null && Size(_allPath) > MaxFileSizeBytes)
            {
                if (Rotation == LogRotationMode.RotateAndZip) RotateFile(ref _allWriter, _allPath);
                else TrimFile(ref _allWriter, _allPath);
            }

            if (_errorWriter != null && Size(_errorPath) > MaxFileSizeBytes)
            {
                if (Rotation == LogRotationMode.RotateAndZip) RotateFile(ref _errorWriter, _errorPath);
                else TrimFile(ref _errorWriter, _errorPath);
            }
        }
        catch (Exception ex)
        {
            LastWriteError = "Log rotation: " + ex.Message;
        }
    }

    private static long Size(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch { return 0; }
    }

    /// <summary>
    /// Closes the file, renames it with a timestamp, opens a fresh file and compresses the old
    /// one to .zip in the background (the .csv is deleted once the zip is written).
    /// </summary>
    private void RotateFile(ref StreamWriter? writer, string path)
    {
        writer?.Flush();
        writer?.Dispose();
        writer = null;

        var directory = Path.GetDirectoryName(path) ?? Directory;
        var name = Path.GetFileNameWithoutExtension(path);
        var stamp = DateTime.Now.ToString("HHmmss");
        var archived = Path.Combine(directory, $"{name}_{stamp}.csv");

        var counter = 1;
        while (File.Exists(archived))
            archived = Path.Combine(directory, $"{name}_{stamp}_{counter++}.csv");

        File.Move(path, archived);
        writer = OpenFile(path);
        Interlocked.Increment(ref _rotations);

        // Compressing can take a moment on a big file - do it off the writer thread.
        _ = Task.Run(() => ZipAndDelete(archived));
    }

    private void ZipAndDelete(string csvPath)
    {
        try
        {
            var zipPath = Path.ChangeExtension(csvPath, ".zip");
            var counter = 1;
            while (File.Exists(zipPath))
                zipPath = Path.ChangeExtension(csvPath, null) + $"_{counter++}.zip";

            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
                zip.CreateEntryFromFile(csvPath, Path.GetFileName(csvPath), CompressionLevel.Optimal);

            File.Delete(csvPath);
        }
        catch (Exception ex)
        {
            LastWriteError = "Log compression: " + ex.Message;
        }
    }

    /// <summary>
    /// Ring buffer: keeps the same file and drops the oldest lines (the two header lines are
    /// kept), so the file stays around the size limit and always holds the newest entries.
    /// </summary>
    private void TrimFile(ref StreamWriter? writer, string path)
    {
        writer?.Flush();
        writer?.Dispose();
        writer = null;

        try
        {
            var lines = File.ReadAllLines(path, Encoding.UTF8);
            var headerCount = 0;
            if (lines.Length > 0 && lines[0].StartsWith("sep=", StringComparison.OrdinalIgnoreCase)) headerCount++;
            if (lines.Length > headerCount) headerCount++;   // column header

            var dataCount = Math.Max(0, lines.Length - headerCount);
            var percent = Math.Clamp(RingTrimPercent, 10, 90);

            // Trim by SIZE, not by a fixed share of the lines: whatever the file grew to, it ends
            // up at (100 - percent)% of the limit, so the newest entries are always kept.
            var target = Math.Max(1024, MaxFileSizeBytes * (100 - percent) / 100);
            long keptBytes = 0;
            var firstKept = lines.Length;
            for (int i = lines.Length - 1; i >= headerCount; i--)
            {
                keptBytes += Encoding.UTF8.GetByteCount(lines[i]) + 2;
                if (keptBytes > target) break;
                firstKept = i;
            }

            var drop = Math.Max(1, firstKept - headerCount);
            if (drop >= dataCount) drop = dataCount;

            var kept = new List<string>(headerCount + dataCount);
            for (int i = 0; i < headerCount; i++) kept.Add(lines[i]);
            kept.Add(BuildTrimNotice(drop));
            for (int i = headerCount + drop; i < lines.Length; i++) kept.Add(lines[i]);

            var temp = path + ".tmp";
            File.WriteAllLines(temp, kept, new UTF8Encoding(true));
            File.Copy(temp, path, overwrite: true);
            File.Delete(temp);

            Interlocked.Increment(ref _rotations);
        }
        finally
        {
            writer = OpenFile(path);
        }
    }

    private string BuildTrimNotice(int droppedLines) => string.Join(_separator,
        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
        "(ring buffer)", "", "TRIMMED", "", "",
        "0", $"Size limit reached - {droppedLines} older line(s) removed");

    private StreamWriter OpenFile(string path)
    {
        var isNew = !File.Exists(path) || new FileInfo(path).Length == 0;
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 1 << 16);
        var writer = new StreamWriter(stream, new UTF8Encoding(true), 1 << 16) { AutoFlush = false };
        if (isNew)
        {
            writer.WriteLine($"sep={_separator}");
            writer.WriteLine(string.Join(_separator,
                "Timestamp", "Device", "Host", "Result", "ResponseMs", "Status", "ConsecutiveFails", "Info"));
        }
        return writer;
    }

    private string Format(StringBuilder sb, in LogRecord r)
    {
        sb.Clear();
        sb.Append(r.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append(_separator);
        sb.Append(Escape(r.DeviceName)).Append(_separator);
        sb.Append(Escape(r.Host)).Append(_separator);
        sb.Append(ResultText(r.Event)).Append(_separator);
        sb.Append(r.IsSuccess || r.Event == PingEvent.Recovered
            ? r.RoundtripMs.ToString(CultureInfo.InvariantCulture)
            : string.Empty).Append(_separator);
        sb.Append(Escape(r.Status)).Append(_separator);
        sb.Append(r.ConsecutiveFails.ToString(CultureInfo.InvariantCulture)).Append(_separator);
        sb.Append(Escape(r.Info ?? string.Empty));
        return sb.ToString();
    }

    private static string ResultText(PingEvent e) => e switch
    {
        PingEvent.Success => "OK",
        PingEvent.Failure => "FAIL",
        PingEvent.SuppressionStarted => "FAIL",
        PingEvent.Recovered => "RECOVERED",
        PingEvent.Paused => "PAUSED",
        PingEvent.Resumed => "RESUMED",
        _ => "?"
    };

    private string Escape(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var needsQuotes = value.Contains(_separator, StringComparison.Ordinal)
                          || value.Contains('"') || value.Contains('\n') || value.Contains('\r');
        if (!needsQuotes) return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>Deletes log files older than the given number of days. 0 = do nothing.</summary>
    public void CleanupOldFiles(int retentionDays)
    {
        if (retentionDays <= 0) return;
        try
        {
            var limit = DateTime.Now.Date.AddDays(-retentionDays);
            var files = System.IO.Directory.EnumerateFiles(Directory, "*.csv")
                .Concat(System.IO.Directory.EnumerateFiles(Directory, "*.zip"));
            foreach (var file in files)
            {
                if (File.GetLastWriteTime(file) < limit)
                {
                    try { File.Delete(file); } catch { /* file may be open */ }
                }
            }
        }
        catch (Exception ex)
        {
            LastWriteError = ex.Message;
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Close the channel and let the writer drain before cancelling hard.
        _channel.Writer.TryComplete();
        var finished = await Task.WhenAny(_worker, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
        if (finished != _worker)
        {
            _cts.Cancel();
            try { await _worker.ConfigureAwait(false); } catch { /* ignored */ }
        }
        _cts.Dispose();
    }
}
