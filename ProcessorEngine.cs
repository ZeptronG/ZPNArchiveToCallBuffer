using System.Buffers;
using System.Collections.Concurrent;
using System.Formats.Tar;
using System.Text;
using System.Threading.Channels;

namespace ZPNArchiveToCallBuffer;

internal sealed record ProcessorOptions(
    string Source, string Destination, string Temp, string LogFolder, int Workers,
    bool Recurse, bool ClearOldLogs, bool DeleteTar, decimal LowSpaceGb, bool DebugHigh);

internal sealed record EngineMessage(string Text, Color Color, bool Important = false);

internal sealed class ProcessorEngine : IAsyncDisposable
{
    private const int CopyBufferSize = 256 * 1024;
    private readonly ProcessorOptions _o;
    private readonly Action<EngineMessage> _report;
    private readonly Channel<LogRow> _logs = Channel.CreateBounded<LogRow>(new BoundedChannelOptions(4096)
    { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource _storageStop = new();
    private readonly ConcurrentDictionary<string, byte> _completedTars = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _tarStates = new(StringComparer.OrdinalIgnoreCase);
    private Task? _logTask;
    private CancellationToken _userToken;
    private long _found, _remaining, _transferred, _reservedBytes;
    private int _storageTriggered;
    private string _logPath = "";

    internal ProcessorEngine(ProcessorOptions options, Action<EngineMessage> report)
    { _o = options; _report = report; }

    internal bool StorageStopped => Volatile.Read(ref _storageTriggered) != 0;

    internal async Task RunAsync(CancellationToken userToken)
    {
        _userToken = userToken;
        Directory.CreateDirectory(_o.Destination);
        Directory.CreateDirectory(_o.Temp);
        Directory.CreateDirectory(_o.LogFolder);
        if (_o.ClearOldLogs)
            foreach (string pattern in new[] { "Audiolog_*.csv", "LegacyAudio_*.csv" })
                foreach (string file in Directory.EnumerateFiles(_o.LogFolder, pattern))
                    try { File.Delete(file); } catch (Exception ex) { Report($"Could not clear log {file}: {ex.Message}", Color.Gold, true); }
        else LoadCompletedTars();

        _logPath = Path.Combine(_o.LogFolder, $"Audiolog_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
        _logTask = WriteLogsAsync();
        await LogAsync(new("INFO", "", "", "", "", "PROCESSING_STARTED", "Processing started"), true, userToken);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(userToken, _storageStop.Token);
        CancellationToken token = linked.Token;
        try
        {
            var tarQueue = Channel.CreateBounded<string>(new BoundedChannelOptions(Math.Max(4, _o.Workers * 2))
            { SingleWriter = true, SingleReader = false, FullMode = BoundedChannelFullMode.Wait });
            Task producer = ProduceTarsAsync(tarQueue.Writer, token);
            Task[] workers = Enumerable.Range(0, _o.Workers)
                .Select(id => ConsumeTarsAsync(id + 1, tarQueue.Reader, token)).ToArray();
            // Await the producer and every worker as one group. On cancellation,
            // Task.WhenAll does not finish until all active operations have exited.
            await Task.WhenAll(workers.Prepend(producer));
            if (!StorageStopped) await ProcessStandaloneAsync(token);
        }
        catch (OperationCanceledException) when (StorageStopped)
        { Report("IT RAN OUT OF STORAGE", Color.Red, true); }
        catch (OperationCanceledException) when (userToken.IsCancellationRequested)
        { Report("Processing cancelled by user.", Color.Orange, true); }
        finally
        {
            string status = StorageStopped ? "PROCESSING_STORAGE_STOP" : userToken.IsCancellationRequested ? "PROCESSING_CANCELLED" : "PROCESSING_COMPLETE";
            await LogAsync(new("INFO", "", "", "", "", status,
                $"Found={Interlocked.Read(ref _found)} Remaining={Interlocked.Read(ref _remaining)} Transferred={Interlocked.Read(ref _transferred)}"), true, CancellationToken.None);
            _logs.Writer.TryComplete();
            if (_logTask is not null) await _logTask;
            await WriteProcessInventoryAsync(userToken.IsCancellationRequested);
            Report($"{status}. Found: {_found:N0}; remaining: {_remaining:N0}; transferred: {_transferred:N0}",
                StorageStopped ? Color.Red : Color.LightGreen, true);
        }
    }

    private async Task ProduceTarsAsync(ChannelWriter<string> writer, CancellationToken token)
    {
        try
        {
            var option = _o.Recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            int seen = 0;
            foreach (string tar in Directory.EnumerateFiles(_o.Source, "*.tar", option))
            {
                token.ThrowIfCancellationRequested();
                string full = Path.GetFullPath(tar);
                if (_completedTars.ContainsKey(full)) { Debug($"Skipping completed TAR: {full}"); continue; }
                _tarStates[full] = "QUEUED";
                await writer.WriteAsync(full, token);
                if (++seen <= 20 || seen % 100 == 0) Report($"Queued TAR {seen:N0}: {Path.GetFileName(tar)}", Color.Cyan);
            }
        }
        finally { writer.TryComplete(); }
    }

    private async Task ConsumeTarsAsync(int worker, ChannelReader<string> reader, CancellationToken token)
    {
        await foreach (string tar in reader.ReadAllAsync(token))
        {
            token.ThrowIfCancellationRequested();
            _tarStates[tar] = "PROCESSING";
            try { await ProcessTarAsync(tar, worker, token); }
            catch (OperationCanceledException) { _tarStates[tar] = "INTERRUPTED_NEEDS_PROCESSING"; throw; }
            catch (Exception ex)
            {
                _tarStates[tar] = "ERROR_NEEDS_PROCESSING";
                Report($"TAR ERROR {Path.GetFileName(tar)}: {ex.Message}", Color.Red, true);
                await LogAsync(new("ERROR", "", Path.GetFileName(tar), tar, "", "TAR_ERROR", ex.Message), true, CancellationToken.None);
            }
        }
    }

    private async Task ProcessTarAsync(string tarPath, int worker, CancellationToken token)
    {
        Report($"Worker {worker}: scanning {Path.GetFileName(tarPath)}", Color.DeepSkyBlue);
        var pairs = new Dictionary<string, PairPlan>(StringComparer.OrdinalIgnoreCase);
        await using (var fs = OpenRead(tarPath))
        using (var reader = new TarReader(fs, leaveOpen: false))
        {
            TarEntry? e;
            while ((e = reader.GetNextEntry(copyData: false)) is not null)
            {
                token.ThrowIfCancellationRequested();
                if (e.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)) continue;
                string ext = Path.GetExtension(e.Name);
                if (!ext.Equals(".wav", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".xml", StringComparison.OrdinalIgnoreCase)) continue;
                string inum = Path.GetFileNameWithoutExtension(e.Name);
                if (!IsValidInum(inum)) continue;
                if (!pairs.TryGetValue(inum, out PairPlan? p)) pairs[inum] = p = new(inum);
                long len = e.Length;
                if (ext.Equals(".wav", StringComparison.OrdinalIgnoreCase)) { p.WavCount++; p.WavName = e.Name; p.WavLength = len; }
                else { p.XmlCount++; p.XmlName = e.Name; p.XmlLength = len; }
            }
        }

        foreach (PairPlan p in pairs.Values)
        {
            Interlocked.Increment(ref _found); Interlocked.Increment(ref _remaining);
            string state = p.WavCount == 0 ? "NOT_TRANSFERRED_MISSING_WAV" : p.XmlCount == 0 ? "NOT_TRANSFERRED_MISSING_XML" :
                p.WavCount > 1 || p.XmlCount > 1 ? "NOT_TRANSFERRED_DUPLICATE" : "QUEUED";
            await LogPairAsync(p.Inum, tarPath, state, "Pair indexed", true, token);
            if (state != "QUEUED") Interlocked.Decrement(ref _remaining);
        }
        var valid = pairs.Values.Where(p => p.WavCount == 1 && p.XmlCount == 1).ToDictionary(p => p.Inum, StringComparer.OrdinalIgnoreCase);
        if (valid.Count == 0) { await CompleteTarAsync(tarPath, true, token); return; }

        var active = new Dictionary<string, Reservation>(StringComparer.OrdinalIgnoreCase);
        bool clean = true;
        try
        {
            await using var fs = OpenRead(tarPath);
            using var reader = new TarReader(fs, leaveOpen: false);
            TarEntry? e;
            while ((e = reader.GetNextEntry(copyData: false)) is not null)
            {
                token.ThrowIfCancellationRequested();
                if (e.DataStream is null) continue;
                string inum = Path.GetFileNameWithoutExtension(e.Name);
                if (!valid.TryGetValue(inum, out PairPlan? p)) continue;
                if (!active.TryGetValue(inum, out Reservation? reservation))
                {
                    reservation = await TryReserveAsync(p.WavLength + p.XmlLength, token);
                    if (reservation is null) throw new OperationCanceledException(token);
                    active[inum] = reservation;
                    await LogPairAsync(inum, tarPath, "TRANSFERRING", "Writing pair", true, token);
                }
                string destination = GetDestinationDirectory(inum);
                Directory.CreateDirectory(destination);
                string output = Path.Combine(destination, Path.GetFileName(e.Name));
                if (File.Exists(output)) throw new IOException($"Destination already exists: {output}");
                // A storage stop prevents new reservations but lets an already-started
                // pair finish. A user Stop still interrupts each buffered read/write.
                await CopyEntryAsync(e.DataStream, output, e.ModificationTime.UtcDateTime, _userToken);
                if (++reservation.FilesWritten == 2)
                {
                    await LogPairAsync(inum, tarPath, "TRANSFERRED", "WAV and XML transferred", true, token, destination);
                    Interlocked.Increment(ref _transferred); Interlocked.Decrement(ref _remaining);
                    reservation.Dispose(); active.Remove(inum); valid.Remove(inum);
                }
            }
            if (valid.Count != 0) clean = false;
        }
        catch { clean = false; throw; }
        finally { foreach (Reservation r in active.Values) r.Dispose(); }
        await CompleteTarAsync(tarPath, clean, token);
    }

    private async Task CompleteTarAsync(string tarPath, bool clean, CancellationToken token)
    {
        if (!clean || StorageStopped || token.IsCancellationRequested) return;
        await LogAsync(new("INFO", "", Path.GetFileName(tarPath), tarPath, "", "TAR_COMPLETE", "TAR completed"), true, token);
        _completedTars.TryAdd(Path.GetFullPath(tarPath), 0);
        _tarStates[tarPath] = "COMPLETED";
        Report($"TAR COMPLETE: {Path.GetFileName(tarPath)}", Color.LightGreen);
        if (_o.DeleteTar) { File.Delete(tarPath); Report($"Deleted completed TAR: {tarPath}", Color.OrangeRed, true); }
    }

    private async Task ProcessStandaloneAsync(CancellationToken token)
    {
        var option = _o.Recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        foreach (string wav in Directory.EnumerateFiles(_o.Source, "*.wav", option))
        {
            token.ThrowIfCancellationRequested();
            string inum = Path.GetFileNameWithoutExtension(wav);
            if (!IsValidInum(inum)) continue;
            string xml = Path.ChangeExtension(wav, ".xml");
            Interlocked.Increment(ref _found); Interlocked.Increment(ref _remaining);
            if (!File.Exists(xml)) { await LogPairAsync(inum, "", "NOT_TRANSFERRED_MISSING_XML", "Standalone XML missing", true, token); Interlocked.Decrement(ref _remaining); continue; }
            await LogPairAsync(inum, "", "QUEUED", "Standalone pair queued", true, token);
            var a = new FileInfo(wav); var b = new FileInfo(xml);
            using Reservation? reserve = await TryReserveAsync(a.Length + b.Length, token);
            if (reserve is null) throw new OperationCanceledException(token);
            await LogPairAsync(inum, "", "TRANSFERRING", "Writing standalone pair", true, token);
            string dir = GetDestinationDirectory(inum); Directory.CreateDirectory(dir);
            await CopyFileAsync(wav, Path.Combine(dir, a.Name), _userToken);
            await CopyFileAsync(xml, Path.Combine(dir, b.Name), _userToken);
            await LogPairAsync(inum, "", "TRANSFERRED", "Standalone pair transferred", true, token, dir);
            Interlocked.Increment(ref _transferred); Interlocked.Decrement(ref _remaining);
        }
    }

    private async Task<Reservation?> TryReserveAsync(long bytes, CancellationToken token)
    {
        long reserved = Interlocked.Add(ref _reservedBytes, bytes);
        try
        {
            string root = Path.GetPathRoot(Path.GetFullPath(_o.Destination))!;
            long free = new DriveInfo(root).AvailableFreeSpace;
            long floor = (long)(_o.LowSpaceGb * 1024m * 1024m * 1024m);
            if (free - reserved < floor)
            {
                if (Interlocked.Exchange(ref _storageTriggered, 1) == 0)
                {
                    Report("IT RAN OUT OF STORAGE", Color.Red, true);
                    await LogAsync(new("ERROR", "", "", "", "", "STORAGE_STOP_REQUESTED", "IT RAN OUT OF STORAGE"), true, CancellationToken.None);
                    _storageStop.Cancel();
                }
                Interlocked.Add(ref _reservedBytes, -bytes);
                return null;
            }
            return new Reservation(this, bytes);
        }
        catch
        {
            Interlocked.Add(ref _reservedBytes, -bytes);
            throw;
        }
    }

    private sealed class Reservation : IDisposable
    {
        private ProcessorEngine? _owner; private readonly long _bytes;
        internal int FilesWritten;
        internal Reservation(ProcessorEngine owner, long bytes) { _owner = owner; _bytes = bytes; }
        public void Dispose() { var o = Interlocked.Exchange(ref _owner, null); if (o is not null) Interlocked.Add(ref o._reservedBytes, -_bytes); }
    }

    private static FileStream OpenRead(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
        CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static async Task CopyEntryAsync(Stream source, string output, DateTime timestamp, CancellationToken token)
    {
        string partial = output + ".partial";
        try
        {
            // Close the destination handle before the atomic rename. Windows will
            // reject File.Move while this FileStream still owns the .partial file.
            await using (var dest = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                byte[] buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
                try
                {
                    int n;
                    while ((n = await source.ReadAsync(buffer, token)) != 0)
                        await dest.WriteAsync(buffer.AsMemory(0, n), token);
                    await dest.FlushAsync(token);
                }
                finally { ArrayPool<byte>.Shared.Return(buffer); }
            }
            File.Move(partial, output); File.SetLastWriteTimeUtc(output, timestamp);
        }
        catch { try { File.Delete(partial); } catch { } throw; }
    }

    private static async Task CopyFileAsync(string source, string output, CancellationToken token)
    {
        if (File.Exists(output)) throw new IOException($"Destination already exists: {output}");
        await using var input = OpenRead(source);
        await CopyEntryAsync(input, output, File.GetLastWriteTimeUtc(source), token);
    }

    private string GetDestinationDirectory(string inum) => Path.Combine(_o.Destination,
        inum[..6], inum.Substring(6, 3), inum.Substring(9, 2), inum.Substring(11, 2));
    private static bool IsValidInum(string s) => s.Length == 15 && s.All(char.IsDigit);

    private void LoadCompletedTars()
    {
        foreach (string pattern in new[] { "Audiolog_*.csv", "VerintAudio_*.csv" })
        foreach (string file in Directory.EnumerateFiles(_o.LogFolder, pattern))
        {
            try
            {
                foreach (string line in File.ReadLines(file).Skip(1))
                {
                    string[] c = ParseCsv(line);
                    if (c.Length >= 7 && c[5].Equals("TAR_COMPLETE", StringComparison.OrdinalIgnoreCase) && c[3].Length > 0)
                        _completedTars.TryAdd(Path.GetFullPath(c[3]), 0);
                }
            }
            catch { }
        }
    }

    private async Task WriteLogsAsync()
    {
        await using var fs = new FileStream(_logPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var writer = new StreamWriter(fs, new UTF8Encoding(true), 64 * 1024);
        await writer.WriteLineAsync("DateTime,LogLevel,INUM,TARFile,TARPath,PairStatus,FoundTotal,Remaining,Status,Destination,FreeSpaceGB,Message");
        int pending = 0;
        await foreach (LogRow item in _logs.Reader.ReadAllAsync())
        {
            await writer.WriteLineAsync(item.ToCsv(Interlocked.Read(ref _found), Interlocked.Read(ref _remaining), FreeSpaceGb()));
            if (item.Flush || ++pending >= 64) { await writer.FlushAsync(); pending = 0; item.Ack?.TrySetResult(); }
            else item.Ack?.TrySetResult();
        }
        await writer.FlushAsync();
    }

    private async Task WriteProcessInventoryAsync(bool stoppedByUser)
    {
        string path = Path.Combine(_o.LogFolder, "Proccess.log");
        var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string tar in _completedTars.Keys) all.Add(Path.GetFullPath(tar));
        foreach (string tar in _tarStates.Keys) all.Add(Path.GetFullPath(tar));
        try
        {
            var option = _o.Recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            foreach (string tar in Directory.EnumerateFiles(_o.Source, "*.tar", option))
                all.Add(Path.GetFullPath(tar));
        }
        catch (Exception ex) { Report("Process inventory scan warning: " + ex.Message, Color.Gold, true); }

        string[] completed = all.Where(t => _completedTars.ContainsKey(t)).OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToArray();
        string[] interrupted = all.Where(t => !_completedTars.ContainsKey(t) &&
            _tarStates.TryGetValue(t, out string? s) && (s.StartsWith("INTERRUPTED") || s == "PROCESSING"))
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToArray();
        string[] pending = all.Where(t => !_completedTars.ContainsKey(t) && !interrupted.Contains(t, StringComparer.OrdinalIgnoreCase))
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToArray();

        await using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var w = new StreamWriter(fs, new UTF8Encoding(true));
        await w.WriteLineAsync("ZPN - Archive to Call Buffer V4.3.19 - TAR Processing Inventory");
        await w.WriteLineAsync($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        await w.WriteLineAsync($"Run stopped by user: {(stoppedByUser ? "YES" : "NO")}");
        await w.WriteLineAsync();
        await w.WriteLineAsync("=== SETTINGS USED FOR THIS PROCESS ===");
        await w.WriteLineAsync($"Source: {_o.Source}");
        await w.WriteLineAsync($"Destination: {_o.Destination}");
        await w.WriteLineAsync($"Temp: {_o.Temp}");
        await w.WriteLineAsync($"Log folder: {_o.LogFolder}");
        await w.WriteLineAsync($"Workers: {_o.Workers}");
        await w.WriteLineAsync($"Recursive search: {(_o.Recurse ? "YES" : "NO")}");
        await w.WriteLineAsync($"Clear old logs: {(_o.ClearOldLogs ? "YES" : "NO")}");
        await w.WriteLineAsync($"Logging mode: {(_o.DebugHigh ? "Debug High" : "Normal")}");
        await w.WriteLineAsync($"Low-space reserve GB: {_o.LowSpaceGb:F1}");
        await w.WriteLineAsync($"Delete TAR after completion: {(_o.DeleteTar ? "YES" : "NO")}");
        await w.WriteLineAsync();
        await w.WriteLineAsync($"Completed TARs: {completed.Length}");
        await w.WriteLineAsync($"Interrupted/in-progress TARs: {interrupted.Length}");
        await w.WriteLineAsync($"TARs still needing processing: {interrupted.Length + pending.Length}");
        await w.WriteLineAsync();
        await w.WriteLineAsync("=== COMPLETED TAR FILES ===");
        foreach (string tar in completed) await w.WriteLineAsync(tar);
        await w.WriteLineAsync();
        await w.WriteLineAsync("=== LAST ACTIVE / INTERRUPTED TAR FILES ===");
        if (interrupted.Length == 0) await w.WriteLineAsync("(none)");
        else foreach (string tar in interrupted) await w.WriteLineAsync(tar);
        await w.WriteLineAsync();
        await w.WriteLineAsync("=== TAR FILES THAT STILL NEED PROCESSING ===");
        foreach (string tar in interrupted.Concat(pending).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string state = _tarStates.TryGetValue(tar, out string? value) ? value : "NOT_STARTED";
            await w.WriteLineAsync($"[{state}] {tar}");
        }
        await w.FlushAsync();
        Report($"Process inventory written: {path}", Color.LightGreen, true);
    }

    private async ValueTask LogPairAsync(string inum, string tar, string status, string message, bool flush, CancellationToken token, string destination = "") =>
        await LogAsync(new(status.StartsWith("NOT_") ? "ERROR" : "INFO", inum, Path.GetFileName(tar), tar, destination, status, message), flush, token);

    private async ValueTask LogAsync(LogRow row, bool flush, CancellationToken token)
    {
        row.Flush = flush;
        if (flush) row.Ack = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await _logs.Writer.WriteAsync(row, token);
        if (row.Ack is not null) await row.Ack.Task.WaitAsync(token);
    }

    private double FreeSpaceGb()
    { try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(_o.Destination))!).AvailableFreeSpace / 1073741824d; } catch { return -1; } }
    private void Debug(string text) { if (_o.DebugHigh) Report(text, Color.DarkGray); }
    private void Report(string text, Color color, bool important = false) => _report(new(text, color, important));

    private sealed record PairPlan(string Inum)
    { internal int WavCount, XmlCount; internal string WavName = "", XmlName = ""; internal long WavLength, XmlLength; }

    private sealed record LogRow(string Level, string Inum, string TarFile, string TarPath, string Destination, string Status, string Message)
    {
        internal bool Flush; internal TaskCompletionSource? Ack;
        internal string ToCsv(long found, long remaining, double free) => string.Join(',', new[]
        { DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"), Level, ExcelText(Inum), TarFile, TarPath, Status, found.ToString(), remaining.ToString(), Status, Destination, free.ToString("F3"), Message }.Select(Csv));
        // Excel's General format renders 15-digit identifiers in scientific
        // notation. A digits-only formula cell preserves and displays every digit.
        private static string ExcelText(string s) => s.Length == 0 ? "" : $"=\"{s}\"";
        private static string Csv(string s) => '"' + s.Replace("\"", "\"\"") + '"';
    }
    private static string[] ParseCsv(string line)
    {
        var result = new List<string>(); var b = new StringBuilder(); bool quote = false;
        for (int i = 0; i < line.Length; i++) { char c = line[i]; if (c == '"') { if (quote && i + 1 < line.Length && line[i + 1] == '"') { b.Append('"'); i++; } else quote = !quote; } else if (c == ',' && !quote) { result.Add(b.ToString()); b.Clear(); } else b.Append(c); }
        result.Add(b.ToString()); return result.ToArray();
    }
    public async ValueTask DisposeAsync() { _storageStop.Dispose(); if (_logTask is not null) await _logTask.ConfigureAwait(false); }
}
