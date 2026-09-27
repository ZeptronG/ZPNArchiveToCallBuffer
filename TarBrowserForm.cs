using System.Buffers;
using System.Formats.Tar;

namespace ZPNArchiveToCallBuffer;

internal sealed class TarBrowserForm : Form
{
    private const int BufferSize = 256 * 1024;
    private readonly TextBox tarPath = new() { Dock = DockStyle.Fill, ReadOnly = true };
    private readonly TextBox search = new() { Dock = DockStyle.Fill, PlaceholderText = "Filter by filename or path..." };
    private readonly Button browse = new() { Text = "Open TAR...", AutoSize = true };
    private readonly Button extract = new() { Text = "Extract Selected Files...", AutoSize = true, Enabled = false };
    private readonly Button cancel = new() { Text = "Cancel", AutoSize = true, Enabled = false };
    private readonly Button close = new() { Text = "Close", AutoSize = true };
    private readonly Label status = new() { Text = "Select a TAR file to inspect.", AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly ListView entries = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true,
        HideSelection = false, MultiSelect = true, VirtualMode = true
    };
    private List<TarItem> allItems = new();
    private List<int> visibleItems = new();
    private CancellationTokenSource? operation;
    private string openedTar = "";

    internal TarBrowserForm()
    {
        Text = "TAR Browser - ZPN V4.3.19"; StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new(820, 520); Size = new(1050, 700); AutoScaleMode = AutoScaleMode.Font;
        entries.Columns.Add("Entry", 520); entries.Columns.Add("Type", 130);
        entries.Columns.Add("Size", 120, HorizontalAlignment.Right); entries.Columns.Add("Modified", 180);
        BuildUi();
        browse.Click += Browse_Click; extract.Click += Extract_Click; cancel.Click += (_, _) => operation?.Cancel();
        close.Click += (_, _) => Close(); search.TextChanged += (_, _) => ApplyFilter();
        entries.RetrieveVirtualItem += RetrieveItem; entries.SelectedIndexChanged += (_, _) => UpdateExtractButton();
        entries.DoubleClick += async (_, _) => await ExtractSelectedAsync();
        FormClosing += (_, _) => operation?.Cancel();
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(10), ColumnCount = 1, RowCount = 5 };
        root.ColumnStyles.Add(new(SizeType.Percent, 100)); root.RowStyles.Add(new(SizeType.AutoSize));
        root.RowStyles.Add(new(SizeType.AutoSize)); root.RowStyles.Add(new(SizeType.Percent, 100));
        root.RowStyles.Add(new(SizeType.AutoSize)); root.RowStyles.Add(new(SizeType.AutoSize));
        var pathRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
        pathRow.ColumnStyles.Add(new(SizeType.Percent, 100)); pathRow.ColumnStyles.Add(new(SizeType.AutoSize));
        pathRow.Controls.Add(tarPath, 0, 0); pathRow.Controls.Add(browse, 1, 0);
        root.Controls.Add(pathRow, 0, 0); root.Controls.Add(search, 0, 1); root.Controls.Add(entries, 0, 2);
        root.Controls.Add(status, 0, 3);
        var buttonRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        buttonRow.Controls.Add(close); buttonRow.Controls.Add(cancel); buttonRow.Controls.Add(extract); root.Controls.Add(buttonRow, 0, 4);
        Controls.Add(root); AcceptButton = extract; CancelButton = close;
    }

    private async void Browse_Click(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog { Title = "Select TAR file", Filter = "TAR archives (*.tar)|*.tar|All files (*.*)|*.*", CheckFileExists = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        await LoadTarAsync(dialog.FileName);
    }

    private async Task LoadTarAsync(string path)
    {
        SetBusy(true, "Reading TAR directory..."); allItems.Clear(); visibleItems.Clear(); entries.VirtualListSize = 0;
        operation = new CancellationTokenSource();
        try
        {
            var progress = new Progress<int>(count => status.Text = $"Reading TAR directory... {count:N0} entries");
            List<TarItem> result = await Task.Run(() => IndexTar(path, progress, operation.Token));
            openedTar = path; tarPath.Text = path; allItems = result; ApplyFilter();
            status.Text = $"{allItems.Count:N0} entries. Select one or more WAV, XML, or other regular files to extract.";
        }
        catch (OperationCanceledException) { status.Text = "TAR reading cancelled."; }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not read TAR", MessageBoxButtons.OK, MessageBoxIcon.Error); status.Text = "TAR could not be read."; }
        finally { operation.Dispose(); operation = null; SetBusy(false); }
    }

    private static List<TarItem> IndexTar(string path, IProgress<int> progress, CancellationToken token)
    {
        var result = new List<TarItem>(); int ordinal = 0;
        using var stream = OpenTar(path); using var reader = new TarReader(stream, leaveOpen: false);
        TarEntry? entry;
        while ((entry = reader.GetNextEntry(copyData: false)) is not null)
        {
            token.ThrowIfCancellationRequested();
            bool regular = entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile;
            result.Add(new TarItem(ordinal++, entry.Name, entry.EntryType.ToString(), regular ? entry.Length : 0,
                entry.ModificationTime.LocalDateTime, regular));
            if (result.Count % 1000 == 0) progress.Report(result.Count);
        }
        return result;
    }

    private void ApplyFilter()
    {
        string value = search.Text.Trim();
        visibleItems = value.Length == 0 ? Enumerable.Range(0, allItems.Count).ToList() :
            Enumerable.Range(0, allItems.Count).Where(i => allItems[i].Name.Contains(value, StringComparison.OrdinalIgnoreCase)).ToList();
        entries.VirtualListSize = visibleItems.Count; entries.Invalidate(); UpdateExtractButton();
    }

    private void RetrieveItem(object? sender, RetrieveVirtualItemEventArgs e)
    {
        if ((uint)e.ItemIndex >= (uint)visibleItems.Count) { e.Item = new ListViewItem(""); return; }
        TarItem item = allItems[visibleItems[e.ItemIndex]];
        e.Item = new ListViewItem(new[] { item.Name, item.Type, item.IsRegular ? FormatSize(item.Length) : "", item.Modified.ToString("yyyy-MM-dd HH:mm:ss") });
    }

    private async void Extract_Click(object? sender, EventArgs e) => await ExtractSelectedAsync();

    private async Task ExtractSelectedAsync()
    {
        if (entries.SelectedIndices.Count == 0 || openedTar.Length == 0) return;
        TarItem[] selected = entries.SelectedIndices.Cast<int>().Where(i => (uint)i < (uint)visibleItems.Count)
            .Select(i => allItems[visibleItems[i]]).Where(i => i.IsRegular).OrderBy(i => i.Ordinal).ToArray();
        if (selected.Length == 0) return;
        using var folder = new FolderBrowserDialog { Description = "Select destination for the extracted file", UseDescriptionForTitle = true, ShowNewFolderButton = true };
        if (folder.ShowDialog(this) != DialogResult.OK) return;
        var outputs = new Dictionary<int, string>();
        foreach (TarItem item in selected)
        {
            string safeName = Path.GetFileName(item.Name.Replace('/', Path.DirectorySeparatorChar));
            if (!string.IsNullOrWhiteSpace(safeName)) outputs[item.Ordinal] = Path.Combine(folder.SelectedPath, safeName);
        }
        string[] duplicateNames = outputs.Values.GroupBy(p => p, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => Path.GetFileName(g.Key)).ToArray();
        if (duplicateNames.Length > 0)
        {
            MessageBox.Show(this, "The selection contains entries with the same output filename. Deselect duplicates and try again:\n\n" +
                string.Join(Environment.NewLine, duplicateNames.Take(20)), "Duplicate filenames", MessageBoxButtons.OK, MessageBoxIcon.Warning); return;
        }
        string[] existing = outputs.Values.Where(File.Exists).ToArray(); bool overwrite = false;
        if (existing.Length > 0)
        {
            DialogResult answer = MessageBox.Show(this, $"{existing.Length:N0} selected file(s) already exist.\n\nYes: replace all existing files\nNo: skip existing files\nCancel: extract nothing",
                "Existing files", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
            if (answer == DialogResult.Cancel) return; overwrite = answer == DialogResult.Yes;
            if (!overwrite) foreach (string path in existing) outputs.Remove(outputs.First(p => p.Value.Equals(path, StringComparison.OrdinalIgnoreCase)).Key);
        }
        if (outputs.Count == 0) { status.Text = "No files needed extraction."; return; }
        operation = new CancellationTokenSource(); SetBusy(true, $"Extracting {outputs.Count:N0} selected file(s)...");
        try
        {
            var progress = new Progress<(int Done, int Total, string Name)>(p => status.Text = $"Extracting {p.Done:N0} of {p.Total:N0}: {p.Name}");
            int completed = await Task.Run(() => ExtractEntriesAsync(openedTar, selected, outputs, overwrite, progress, operation.Token));
            status.Text = $"Extracted {completed:N0} file(s) to {folder.SelectedPath}";
            MessageBox.Show(this, $"{completed:N0} file(s) extracted successfully:\n\n{folder.SelectedPath}", "Extraction complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (OperationCanceledException) { status.Text = "Extraction cancelled; partial output removed."; }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Extraction failed", MessageBoxButtons.OK, MessageBoxIcon.Error); status.Text = "Extraction failed; partial output removed."; }
        finally { operation.Dispose(); operation = null; SetBusy(false); }
    }

    private static async Task<int> ExtractEntriesAsync(string tar, TarItem[] selected, Dictionary<int, string> outputs, bool overwrite,
        IProgress<(int Done, int Total, string Name)> progress, CancellationToken token)
    {
        var wanted = selected.Where(i => outputs.ContainsKey(i.Ordinal)).ToDictionary(i => i.Ordinal); int completed = 0;
        using var stream = OpenTar(tar); using var reader = new TarReader(stream, leaveOpen: false);
        TarEntry? entry; int ordinal = 0;
        while ((entry = reader.GetNextEntry(copyData: false)) is not null)
        {
            token.ThrowIfCancellationRequested(); int current = ordinal++;
            if (!wanted.TryGetValue(current, out TarItem? item)) continue;
            if (entry.DataStream is null || entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                throw new InvalidDataException($"The selected TAR entry is no longer extractable: {item.Name}");
            string output = outputs[current], partial = output + ".partial";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                await using (var destination = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None,
                    BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
                    try { int read; while ((read = await entry.DataStream.ReadAsync(buffer, token)) != 0) await destination.WriteAsync(buffer.AsMemory(0, read), token); await destination.FlushAsync(token); }
                    finally { ArrayPool<byte>.Shared.Return(buffer); }
                }
                File.Move(partial, output, overwrite); File.SetLastWriteTime(output, item.Modified);
                progress.Report((++completed, wanted.Count, item.Name));
            }
            catch { try { File.Delete(partial); } catch { } throw; }
        }
        if (completed != wanted.Count) throw new InvalidDataException("One or more selected entries were not found. The TAR may have changed since it was opened.");
        return completed;
    }

    private void SetBusy(bool busy, string? text = null)
    {
        browse.Enabled = !busy; search.Enabled = !busy; entries.Enabled = !busy; close.Enabled = !busy;
        cancel.Enabled = busy; if (text is not null) status.Text = text; UpdateExtractButton();
    }
    private void UpdateExtractButton()
    {
        extract.Enabled = operation is null && entries.SelectedIndices.Cast<int>().Any(i =>
            (uint)i < (uint)visibleItems.Count && allItems[visibleItems[i]].IsRegular);
    }
    private static FileStream OpenTar(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan | FileOptions.Asynchronous);
    private static string FormatSize(long bytes) => bytes >= 1073741824 ? $"{bytes / 1073741824d:N2} GB" : bytes >= 1048576 ? $"{bytes / 1048576d:N2} MB" : bytes >= 1024 ? $"{bytes / 1024d:N1} KB" : $"{bytes:N0} B";
    private sealed record TarItem(int Ordinal, string Name, string Type, long Length, DateTime Modified, bool IsRegular);
}
