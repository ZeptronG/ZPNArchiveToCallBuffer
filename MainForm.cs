using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace ZPNArchiveToCallBuffer;

public sealed class MainForm : Form
{
    private readonly TextBox txtSource = PathBox(), txtDestination = PathBox(), txtTemp = PathBox(), txtLog = PathBox();
    private readonly NumericUpDown nudWorkers = new() { Minimum = 1, Maximum = 16, Value = 2, Width = 80 };
    private readonly NumericUpDown nudLowSpace = new() { Minimum = 0.1m, Maximum = 100000, DecimalPlaces = 1, Increment = 0.5m, Value = 10, Width = 110 };
    private readonly CheckBox chkRecurse = new() { Text = "Search source subfolders (recursive)", AutoSize = true, Checked = true };
    private readonly CheckBox chkClearLogs = new() { Text = "Clear old processor logs before starting", AutoSize = true };
    private readonly CheckBox chkDebug = new() { Text = "Debug High (detailed troubleshooting log)", AutoSize = true };
    private readonly CheckBox chkDeleteTar = new() { Text = "Delete TAR after successful completion", AutoSize = true, Enabled = false };
    private readonly Button btnUnlock = new() { Text = "Unlock TAR Delete", AutoSize = true };
    private readonly Label lblDelete = new() { Text = "TAR deletion: Locked", AutoSize = true, ForeColor = Color.DarkRed, Margin = new(8, 7, 3, 3) };
    private readonly Button btnStart = new() { Text = "Start Processing", AutoSize = true, Padding = new(14, 6, 14, 6) };
    private readonly Button btnStop = new() { Text = "Stop", AutoSize = true, Padding = new(14, 6, 14, 6), Enabled = false };
    private readonly Button btnExit = new() { Text = "Exit", AutoSize = true, Padding = new(14, 6, 14, 6), Anchor = AnchorStyles.Right };
    private readonly Button btnTarBrowser = new() { Text = "Browse TAR Contents...", AutoSize = true, Padding = new(8, 4, 8, 4) };
    private readonly RichTextBox txtOutput = new() { Dock = DockStyle.Fill, ReadOnly = true, BackColor = Color.Black, ForeColor = Color.Gainsboro, Font = new("Consolas", 9), DetectUrls = false };
    private readonly System.Windows.Forms.Timer outputTimer = new() { Interval = 100 };
    private readonly ConcurrentQueue<EngineMessage> outputQueue = new();
    private CancellationTokenSource? runCts;
    private Task? runTask;
    private int queuedOutput, suppressedOutput;
    private bool deleteUnlocked;
    private const int MaxQueuedOutput = 4000, MaxTerminalChars = 750_000;
    private const int DeletePasscodeIterations = 200_000;
    private const string DeletePasscodeSaltBase64 = "FRBFgfOaCfSBgI4gfjWDug==";
    private const string DeletePasscodeVerifierBase64 = "isu0DaxZ/btQS330PvTseLoPbHqnSteEP1ugdO2Ngvk=";

    public MainForm()
    {
        Text = "ZPN - Archive to Call Buffer V4.3.19";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        StartPosition = FormStartPosition.CenterScreen; AutoScaleMode = AutoScaleMode.Font;
        MinimumSize = new(900, 650); Size = new(1150, 820);
        BuildUi(); LoadDefaults(); LoadLastConfig();
        outputTimer.Tick += (_, _) => DrainOutput(); outputTimer.Start();
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new(12) };
        root.ColumnStyles.Add(new(SizeType.Percent, 100)); root.RowStyles.Add(new(SizeType.AutoSize)); root.RowStyles.Add(new(SizeType.Percent, 100)); root.RowStyles.Add(new(SizeType.AutoSize));
        var tabs = new TabControl { Dock = DockStyle.Top, Height = 245 };
        var general = new TabPage("General") { Padding = new(8) }; var advanced = new TabPage("Advanced") { Padding = new(8) };
        tabs.TabPages.Add(general); tabs.TabPages.Add(advanced);
        var g = Grid(7); general.Controls.Add(g);
        AddPath(g, 0, "Source:", txtSource); AddPath(g, 1, "Destination:", txtDestination);
        AddDerivedPath(g, 2, "Temp:", txtTemp); AddDerivedPath(g, 3, "Log folder:", txtLog);
        AddControl(g, 4, "Workers:", Flow(nudWorkers, new Label { Text = "fixed TAR workers (2 is conservative)", AutoSize = true, Margin = new(8, 7, 3, 3) }));
        AddControl(g, 5, "Search:", chkRecurse); AddControl(g, 6, "Logs:", chkClearLogs);
        var a = Grid(5); advanced.Controls.Add(a);
        AddControl(a, 0, "Storage stop:", Flow(nudLowSpace, new Label { Text = "GB free-space reserve", AutoSize = true, Margin = new(8, 7, 3, 3) }));
        AddControl(a, 1, "Logging:", chkDebug);
        AddControl(a, 2, "Safety:", Flow(chkDeleteTar, btnUnlock, lblDelete));
        AddControl(a, 3, "", new Label { Text = "TAR deletion stays locked until the deletion passcode is entered and occurs only after full success.", AutoSize = true, ForeColor = Color.DimGray });
        AddControl(a, 4, "TAR tools:", Flow(btnTarBrowser, new Label { Text = "View entries and extract one selected file", AutoSize = true, Margin = new(8, 7, 3, 3) }));
        root.Controls.Add(tabs, 0, 0); root.Controls.Add(txtOutput, 0, 1);
        var buttonBar = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new(0, 8, 0, 0) };
        buttonBar.ColumnStyles.Add(new(SizeType.Percent, 100)); buttonBar.ColumnStyles.Add(new(SizeType.AutoSize));
        buttonBar.Controls.Add(Flow(btnStart, btnStop), 0, 0); buttonBar.Controls.Add(btnExit, 1, 0);
        root.Controls.Add(buttonBar, 0, 2); Controls.Add(root);
        txtDestination.TextChanged += (_, _) => UpdateDerivedPaths();
        btnStart.Click += Start_Click; btnStop.Click += Stop_Click; btnExit.Click += (_, _) => Close();
        btnTarBrowser.Click += (_, _) => { using var browser = new TarBrowserForm(); browser.ShowDialog(this); };
        btnUnlock.Click += Unlock_Click; FormClosing += MainForm_FormClosing;
    }

    private static TableLayoutPanel Grid(int rows)
    {
        var p = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, RowCount = rows };
        p.ColumnStyles.Add(new(SizeType.AutoSize)); p.ColumnStyles.Add(new(SizeType.Percent, 100)); p.ColumnStyles.Add(new(SizeType.AutoSize));
        for (int i = 0; i < rows; i++) p.RowStyles.Add(new(SizeType.AutoSize)); return p;
    }
    private static FlowLayoutPanel Flow(params Control[] controls) { var p = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true }; p.Controls.AddRange(controls); return p; }
    private static TextBox PathBox() => new() { Dock = DockStyle.Fill, MinimumSize = new(300, 0) };
    private static Label SettingLabel(string s) => new() { Text = s, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new(3, 7, 10, 3) };
    private static void AddControl(TableLayoutPanel p, int row, string label, Control c) { p.Controls.Add(SettingLabel(label), 0, row); p.Controls.Add(c, 1, row); p.SetColumnSpan(c, 2); }
    private void AddPath(TableLayoutPanel p, int row, string label, TextBox box)
    {
        var b = new Button { Text = "Browse", AutoSize = true }; b.Click += (_, _) => PickFolder(box);
        p.Controls.Add(SettingLabel(label), 0, row); p.Controls.Add(box, 1, row); p.Controls.Add(b, 2, row);
    }
    private static void AddDerivedPath(TableLayoutPanel p, int row, string label, TextBox box)
    {
        box.ReadOnly = true; box.BackColor = SystemColors.Control;
        p.Controls.Add(SettingLabel(label), 0, row); p.Controls.Add(box, 1, row); p.SetColumnSpan(box, 2);
    }
    private static void PickFolder(TextBox target)
    { using var d = new FolderBrowserDialog { UseDescriptionForTitle = true, Description = "Select folder", ShowNewFolderButton = true, SelectedPath = Directory.Exists(target.Text) ? target.Text : "" }; if (d.ShowDialog() == DialogResult.OK) target.Text = d.SelectedPath; }

    private void LoadDefaults()
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "CallsCreationTest");
        txtSource.Text = Path.Combine(root, "Source"); txtDestination.Text = Path.Combine(root, "Dest"); UpdateDerivedPaths();
        nudWorkers.Value = Math.Clamp(Math.Min(2, Environment.ProcessorCount), 1, 16);
    }

    private void UpdateDerivedPaths()
    {
        string destination = txtDestination.Text.Trim();
        txtTemp.Text = destination.Length == 0 ? "" : Path.Combine(destination, "Temp");
        txtLog.Text = destination.Length == 0 ? "" : Path.Combine(destination, "Logs");
    }

    private static string ConfigPath => Path.Combine(AppContext.BaseDirectory, "ZPN.config");

    private void LoadLastConfig()
    {
        if (!File.Exists(ConfigPath)) return;
        try
        {
            LastRunConfig? config = JsonSerializer.Deserialize<LastRunConfig>(File.ReadAllText(ConfigPath));
            if (config is null) return;
            if (!string.IsNullOrWhiteSpace(config.Source)) txtSource.Text = config.Source;
            if (!string.IsNullOrWhiteSpace(config.Destination)) txtDestination.Text = config.Destination;
            nudWorkers.Value = Math.Clamp(config.Workers, (int)nudWorkers.Minimum, (int)nudWorkers.Maximum);
            nudLowSpace.Value = Math.Clamp(config.LowSpaceGb, nudLowSpace.Minimum, nudLowSpace.Maximum);
            chkRecurse.Checked = config.Recurse;
            chkClearLogs.Checked = config.ClearOldLogs;
            chkDebug.Checked = config.DebugHigh;
            // Deletion is deliberately never re-enabled across launches. The file
            // records prior use, but the passcode must unlock it again each time.
            chkDeleteTar.Checked = false;
            QueueOutput(new($"Loaded last-run configuration: {ConfigPath}", Color.Cyan, true));
        }
        catch (Exception ex) { QueueOutput(new("Could not load ZPN.config: " + ex.Message, Color.Gold, true)); }
    }

    private void SaveLastConfig(ProcessorOptions options)
    {
        var config = new LastRunConfig(options.Source, options.Destination, options.Workers, options.Recurse,
            options.ClearOldLogs, options.LowSpaceGb, options.DebugHigh, options.DeleteTar, DateTime.Now);
        string temporary = ConfigPath + ".tmp";
        try
        {
            string json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(temporary, json);
            File.Move(temporary, ConfigPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(temporary); } catch { }
            throw;
        }
    }

    private bool ValidateSettings()
    {
        if (!Directory.Exists(txtSource.Text)) { MessageBox.Show(this, "The source folder does not exist.", "Invalid source", MessageBoxButtons.OK, MessageBoxIcon.Warning); return false; }
        try
        {
            string source = Path.GetFullPath(txtSource.Text), destination = Path.GetFullPath(txtDestination.Text);
            if (destination.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            { MessageBox.Show(this, "Destination cannot be inside Source because recursive scans could reprocess output.", "Unsafe paths", MessageBoxButtons.OK, MessageBoxIcon.Warning); return false; }
            Directory.CreateDirectory(destination);
            Directory.CreateDirectory(txtTemp.Text);
            Directory.CreateDirectory(txtLog.Text);
        }
        catch { MessageBox.Show(this, "One or more paths are invalid.", "Invalid paths", MessageBoxButtons.OK, MessageBoxIcon.Warning); return false; }
        return true;
    }

    private async void Start_Click(object? sender, EventArgs e)
    {
        if (runTask is { IsCompleted: false } || !ValidateSettings()) return;
        btnStart.Enabled = false; btnStop.Enabled = true; SetSettings(false); txtOutput.Clear();
        while (outputQueue.TryDequeue(out _)) { } queuedOutput = suppressedOutput = 0;
        runCts = new();
        var options = new ProcessorOptions(txtSource.Text, txtDestination.Text, txtTemp.Text, txtLog.Text, (int)nudWorkers.Value,
            chkRecurse.Checked, chkClearLogs.Checked, chkDeleteTar.Checked && deleteUnlocked, nudLowSpace.Value, chkDebug.Checked);
        try { SaveLastConfig(options); }
        catch (Exception ex)
        {
            QueueOutput(new("CONFIG ERROR: Could not save ZPN.config: " + ex.Message, Color.Red, true));
            runCts.Dispose(); runCts = null; btnStart.Enabled = true; btnStop.Enabled = false; SetSettings(true); return;
        }
        QueueOutput(new($"Starting V4.3.19 with {options.Workers} worker(s).", Color.Cyan, true));
        DrainOutput();
        await using var engine = new ProcessorEngine(options, QueueOutput);
        // Run the complete engine on a pool thread. Directory and TAR enumeration
        // can complete synchronously for long stretches, so merely awaiting the
        // engine from this event handler can otherwise occupy the WinForms thread
        // and prevent the terminal timer and Stop button from being serviced.
        runTask = Task.Run(() => engine.RunAsync(runCts.Token), CancellationToken.None);
        try { await runTask; }
        catch (Exception ex) { QueueOutput(new("ENGINE ERROR: " + ex.Message, Color.Red, true)); }
        finally
        {
            DrainOutput(int.MaxValue); runCts.Dispose(); runCts = null; runTask = null;
            btnStart.Enabled = true; btnStop.Enabled = false; SetSettings(true);
        }
    }

    private void Stop_Click(object? sender, EventArgs e)
    {
        if (runCts is null) return;
        if (MessageBox.Show(this, "Stop after the current file-write operation reaches a cancellation point?", "Confirm stop", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
        { btnStop.Enabled = false; runCts.Cancel(); QueueOutput(new("Stop requested...", Color.Orange, true)); }
    }

    private void QueueOutput(EngineMessage message)
    {
        int count = Interlocked.Increment(ref queuedOutput);
        if (count > MaxQueuedOutput && !message.Important) { Interlocked.Decrement(ref queuedOutput); Interlocked.Increment(ref suppressedOutput); return; }
        outputQueue.Enqueue(message);
    }

    private void DrainOutput(int max = 160)
    {
        if (IsDisposed) return;
        int suppressed = Interlocked.Exchange(ref suppressedOutput, 0), n = 0;
        if (suppressed > 0) AppendBatch(new($"TERMINAL THROTTLED: {suppressed:N0} status line(s) suppressed; CSV logging is unaffected.", Color.Gold, true));
        while (n++ < max && outputQueue.TryDequeue(out EngineMessage? m)) { Interlocked.Decrement(ref queuedOutput); AppendBatch(m); }
        if (txtOutput.TextLength > MaxTerminalChars) { txtOutput.Select(0, txtOutput.TextLength - MaxTerminalChars + 100_000); txtOutput.SelectedText = ""; }
        if (n > 1) { txtOutput.SelectionStart = txtOutput.TextLength; txtOutput.ScrollToCaret(); }
    }
    private void AppendBatch(EngineMessage m)
    { txtOutput.SelectionStart = txtOutput.TextLength; txtOutput.SelectionColor = m.Color; txtOutput.AppendText(m.Text + Environment.NewLine); }

    private void SetSettings(bool enabled)
    { foreach (Control c in new Control[] { txtSource, txtDestination, nudWorkers, nudLowSpace, chkRecurse, chkClearLogs, chkDebug, btnUnlock, btnTarBrowser }) c.Enabled = enabled; chkDeleteTar.Enabled = enabled && deleteUnlocked; }

    private void Unlock_Click(object? sender, EventArgs e)
    {
        using var dialog = new Form { Text = "Unlock TAR Deletion", StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, ClientSize = new(360, 125), MaximizeBox = false, MinimizeBox = false };
        var input = new TextBox { Left = 18, Top = 35, Width = 320, UseSystemPasswordChar = true }; var label = new Label { Left = 18, Top = 14, Text = "Enter deletion passcode:", AutoSize = true };
        var ok = new Button { Text = "Unlock", Left = 180, Top = 75, DialogResult = DialogResult.OK }; var cancel = new Button { Text = "Cancel", Left = 263, Top = 75, DialogResult = DialogResult.Cancel };
        dialog.Controls.AddRange(new Control[] { label, input, ok, cancel }); dialog.AcceptButton = ok; dialog.CancelButton = cancel;
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (!VerifyPasscode(input.Text)) { MessageBox.Show(this, "Incorrect passcode.", "Access denied", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        deleteUnlocked = true; chkDeleteTar.Enabled = true; lblDelete.Text = "TAR deletion: Unlocked"; lblDelete.ForeColor = Color.DarkGreen;
    }
    private static bool VerifyPasscode(string passcode)
    {
        byte[] salt = Convert.FromBase64String(DeletePasscodeSaltBase64), expected = Convert.FromBase64String(DeletePasscodeVerifierBase64);
        byte[] actual = Rfc2898DeriveBytes.Pbkdf2(passcode, salt, DeletePasscodeIterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        if (runTask is not { IsCompleted: false }) return;
        if (MessageBox.Show(this, "Processing is running. Request cancellation and close after it stops?", "Processing running", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.No) { e.Cancel = true; return; }
        runCts?.Cancel(); e.Cancel = true;
        _ = runTask.ContinueWith(_ => BeginInvoke(Close), TaskScheduler.Default);
    }

    private sealed record LastRunConfig(string Source, string Destination, int Workers, bool Recurse,
        bool ClearOldLogs, decimal LowSpaceGb, bool DebugHigh, bool DeleteTarUsed, DateTime SavedAt);
}
