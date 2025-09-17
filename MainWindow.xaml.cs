// MainWindow.xaml.cs
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
// Aliases
using WF = System.Windows.Forms;           // NotifyIcon, Screen, ContextMenuStrip
using SD = System.Drawing;                 // SystemIcons, Rectangle

namespace Traycer
{
    public partial class MainWindow : Window
    {
        // ===== Interop =====
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        private struct APPBARDATA
        {
            public uint cbSize;
            public IntPtr hWnd;
            public uint uCallbackMessage;
            public uint uEdge;
            public RECT rc;
            public int lParam;
        }
        [DllImport("shell32.dll")] static extern uint SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);
        const uint ABM_GETTASKBARPOS = 0x00000005;
        const int ABE_LEFT = 0, ABE_TOP = 1, ABE_RIGHT = 2, ABE_BOTTOM = 3;

        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll", SetLastError = true)] static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        const uint SWP_NOACTIVATE = 0x0010, SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_SHOWWINDOW = 0x0040;

        const int GWL_EXSTYLE = -20;
        const int WS_EX_TRANSPARENT = 0x00000020;
        const int WS_EX_TOOLWINDOW = 0x00000080;
        const int WS_EX_LAYERED = 0x00080000;

        const int HOTKEY_ID = 0xBEEF;
        const uint MOD_ALT = 0x0001, MOD_WIN = 0x0008;
        const uint VK_H = 0x48;

        // ===== State =====
        private readonly WF.NotifyIcon _tray;
        private bool _clickThrough = true;
        private IntPtr _hwnd;

        private readonly CancellationTokenSource _cts = new();
        private Task? _pipeTask;

        // wellId -> UI controls
        private readonly Dictionary<string, (Border border, TextBlock text)> _wells = new();
        // wellId -> action command
        private readonly Dictionary<string, string> _actions = new();
        // configured background tasks
        private readonly Dictionary<string, ManagedTask> _tasks = new(StringComparer.OrdinalIgnoreCase);
        private readonly WF.ContextMenuStrip _trayMenu;

        // placement settings (IPC "placement")
        private double? _heightOverride = 26;   // tighter by default
        private double _bottomOffset = 2;
        private double _padding = 6;
        private double _cornerRadius = 8;

        // z-order keep-alive
        private System.Windows.Threading.DispatcherTimer? _topmostTimer;

        public MainWindow()
        {
            InitializeComponent();

            // Try defaults file; if not present, fall back to built-ins
            if (!TryLoadDefaults(out var error))
            {
                var message = string.IsNullOrWhiteSpace(error) ? "Traycer could not load its defaults file." : error;
                try { Console.Error.WriteLine(message); } catch { }
                System.Windows.MessageBox.Show(message, "Traycer", MessageBoxButton.OK, MessageBoxImage.Error);
                Environment.Exit(1);
                return;
            }

            Console.CancelKeyPress += (_, args) =>
            {
                args.Cancel = true;
                Dispatcher.Invoke(() => Close());
            };

            Loaded += OnLoaded;
            Closed += OnClosed;
            Deactivated += (_, __) => ReassertTopmost();

            SystemEvents.DisplaySettingsChanged += (_, __) => PositionOverTaskbarLeftThird();
            SystemEvents.UserPreferenceChanged += (_, __) => PositionOverTaskbarLeftThird();

            // Tray icon
            _tray = new WF.NotifyIcon
            {
                Icon = SD.SystemIcons.Information,
                Text = "Traycer HUD"
            };
            _trayMenu = new WF.ContextMenuStrip();
            _trayMenu.Opening += (_, __) => RefreshTrayMenu();
            _tray.ContextMenuStrip = _trayMenu;
            RefreshTrayMenu();
            _tray.Visible = true;

            ApplyChrome();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _hwnd = new WindowInteropHelper(this).EnsureHandle();
            HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);

            int ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
            ex |= WS_EX_TOOLWINDOW | WS_EX_LAYERED;
            if (_clickThrough) ex |= WS_EX_TRANSPARENT;
            SetWindowLong(_hwnd, GWL_EXSTYLE, ex);

            RegisterHotKey(_hwnd, HOTKEY_ID, MOD_WIN | MOD_ALT, VK_H);

            PositionOverTaskbarLeftThird();
            ReassertTopmost();

            _topmostTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            _topmostTimer.Tick += (_, __) => ReassertTopmost();
            _topmostTimer.Start();

            _pipeTask = Task.Run(PipeLoopAsync);
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            try { UnregisterHotKey(_hwnd, HOTKEY_ID); } catch { }
            _cts.Cancel();
            try { _pipeTask?.Wait(500); } catch { }
            _topmostTimer?.Stop();

            foreach (var task in _tasks.Values)
            {
                try
                {
                    if (task.IsScheduled)
                    {
                        RemoveScheduledTask(task.Config);
                    }
                    else if (task.ActiveProcess is { HasExited: false })
                    {
                        task.ActiveProcess.Kill(true);
                    }
                }
                catch { }
            }
            _tasks.Clear();

            _tray.Visible = false;
            _tray.Dispose();
        }

        // ===== Layout / placement =====
        private void PositionOverTaskbarLeftThird()
        {
            var (_, rectTB) = GetTaskbarRect();
            var screen = WF.Screen.PrimaryScreen!.Bounds;

            double height = (_heightOverride ?? rectTB.Height - (2 * _padding));
            height = Math.Max(20, height);

            double x = screen.Left + _padding;
            double y = screen.Bottom - height - _bottomOffset - _padding;

            Left = x;
            Top = y;
            Height = height;
            ApplyChrome();
        }

        private void ApplyChrome()
        {
            if (RootBorder != null)
            {
                RootBorder.Margin = new Thickness(_padding);
                RootBorder.CornerRadius = new CornerRadius(_cornerRadius);
            }
            if (WellsGrid != null)
            {
                WellsGrid.Margin = new Thickness(_padding);
            }
            foreach (var kv in _wells)
            {
                kv.Value.text.Margin = new Thickness(_padding, Math.Max(1, _padding / 2), _padding, Math.Max(1, _padding / 2));
                kv.Value.border.Margin = new Thickness(Math.Max(2, _padding / 2), 2, Math.Max(2, _padding / 2), 2);
                kv.Value.border.CornerRadius = new CornerRadius(Math.Max(4, _cornerRadius / 2));
            }
            UpdateWindowWidth();
        }

        private void UpdateWindowWidth()
        {
            if (RootBorder == null || WellsGrid == null) return;

            double columnsWidth = 0;
            foreach (var column in WellsGrid.ColumnDefinitions)
            {
                var length = column.Width;
                if (length.IsAbsolute)
                {
                    columnsWidth += length.Value;
                }
                else if (column.ActualWidth > 0)
                {
                    columnsWidth += column.ActualWidth;
                }
            }

            if (columnsWidth <= 0)
            {
                double actual = WellsGrid.ActualWidth;
                if (!double.IsNaN(actual))
                {
                    columnsWidth = actual;
                }
            }

            Thickness gridMargin = WellsGrid.Margin;
            double tintedWidth = columnsWidth + gridMargin.Left + gridMargin.Right;

            Thickness borderMargin = RootBorder.Margin;
            double totalWidth = tintedWidth + borderMargin.Left + borderMargin.Right;

            // Fit the chrome to the wells while keeping a reasonable minimum.
            Width = Math.Max(80, totalWidth);
        }

        private (int edge, SD.Rectangle rect) GetTaskbarRect()
        {
            var abd = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>() };
            SHAppBarMessage(ABM_GETTASKBARPOS, ref abd);
            var r = abd.rc;
            var rect = SD.Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
            int edge = (int)abd.uEdge;

            if (rect.Width == 0 || rect.Height == 0)
            {
                var full = WF.Screen.PrimaryScreen!.Bounds;
                int thickness = Math.Max(40, full.Height - WF.Screen.PrimaryScreen!.WorkingArea.Height);
                rect = new SD.Rectangle(full.Left, full.Bottom - thickness, full.Width, thickness);
                edge = ABE_BOTTOM;
            }
            return (edge, rect);
        }

        private void ReassertTopmost()
        {
            SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOACTIVATE | SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
        }

        // ===== Wells =====
        private record WellConfig(string id, double width);

        private record TaskConfig(string Id, string Command, string? Arguments, string Mode, bool AutoStart, ScheduleConfig? Schedule, string? WorkingDirectory);
        private record ScheduleConfig(string Frequency, int? Interval, string? Start);

        private class ManagedTask
        {
            public ManagedTask(TaskConfig config) => Config = config;
            public TaskConfig Config { get; private set; }
            public Process? ActiveProcess { get; set; }
            public bool IsScheduled => string.Equals(Config.Mode, "schedule", StringComparison.OrdinalIgnoreCase);
            public void Update(TaskConfig config) => Config = config;
        }

        private void ApplyConfig(IEnumerable<WellConfig> configs)
        {
            WellsGrid.ColumnDefinitions.Clear();
            WellsGrid.Children.Clear();
            _wells.Clear();

            int col = 0;
            foreach (var cfg in configs)
            {
                WellsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(cfg.width) });

                var (border, tb) = CreateWellVisual(cfg.id);
                Grid.SetColumn(border, col++);
                WellsGrid.Children.Add(border);
                _wells[cfg.id] = (border, tb);
            }

            RefreshTrayMenu();
        }

        private (Border border, TextBlock tb) CreateWellVisual(string id)
        {
            var border = new Border
            {
                Margin = new Thickness(Math.Max(2, _padding / 2), 2, Math.Max(2, _padding / 2), 2),
                CornerRadius = new CornerRadius(Math.Max(4, _cornerRadius / 2)),
                Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x33, 0x33, 0x33, 0x33)),
                Cursor = System.Windows.Input.Cursors.Hand,
                Tag = id
            };
            var tb = new TextBlock
            {
                Foreground = System.Windows.Media.Brushes.White,
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI Emoji"),
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(_padding, Math.Max(1, _padding / 2), _padding, Math.Max(1, _padding / 2))
            };
            border.Child = tb;
            border.MouseLeftButtonUp += OnWellClick;
            return (border, tb);
        }

        private int? GetColumnOf(string id)
        {
            if (!_wells.TryGetValue(id, out var tuple)) return null;
            return Grid.GetColumn(tuple.border);
        }

        private void AddWell(string id, double width, int? index = null)
        {
            if (_wells.ContainsKey(id))
            {
                ResizeWell(id, width);
                return;
            }
            int insert = Math.Clamp(index ?? WellsGrid.ColumnDefinitions.Count, 0, WellsGrid.ColumnDefinitions.Count);
            WellsGrid.ColumnDefinitions.Insert(insert, new ColumnDefinition { Width = new GridLength(width) });

            // Shift existing children at/after insert right by 1
            foreach (var child in WellsGrid.Children.OfType<Border>())
            {
                int c = Grid.GetColumn(child);
                if (c >= insert) Grid.SetColumn(child, c + 1);
            }

            var (border, tb) = CreateWellVisual(id);
            Grid.SetColumn(border, insert);
            WellsGrid.Children.Add(border);
            _wells[id] = (border, tb);
            ApplyChrome();
            RefreshTrayMenu();
        }

        private void RemoveWell(string id)
        {
            if (!_wells.TryGetValue(id, out var tuple)) return;
            int col = Grid.GetColumn(tuple.border);
            WellsGrid.Children.Remove(tuple.border);
            _wells.Remove(id);
            _actions.Remove(id);

            // Remove column and shift left any to the right
            WellsGrid.ColumnDefinitions.RemoveAt(col);
            foreach (var child in WellsGrid.Children.OfType<Border>())
            {
                int c = Grid.GetColumn(child);
                if (c > col) Grid.SetColumn(child, c - 1);
            }
            ApplyChrome();
            RefreshTrayMenu();
        }

        private void ResizeWell(string id, double width)
        {
            int? col = GetColumnOf(id);
            if (col is null) return;
            WellsGrid.ColumnDefinitions[col.Value].Width = new GridLength(width);
            UpdateWindowWidth();
        }

        private void OnWellClick(object sender, MouseButtonEventArgs e)
        {
            if (_clickThrough) return;
            if (sender is not Border b || b.Tag is not string id) return;
            if (!_actions.TryGetValue(id, out var action) || string.IsNullOrWhiteSpace(action)) return;

            ExecuteActionCommand(action);
        }

        private void SetWell(string id, string? text = null, string? fg = null, string? bg = null, bool? blink = null, string? action = null)
        {
            if (!_wells.TryGetValue(id, out var tuple)) return;
            var (border, tb) = tuple;

            if (text is not null) tb.Text = text;
            if (fg is not null && TryParseColor(fg, out var c)) tb.Foreground = new SolidColorBrush(c);
            if (bg is not null && TryParseColor(bg, out var c2)) border.Background = new SolidColorBrush(c2);
            if (blink is not null) tb.FontWeight = blink.Value ? FontWeights.Bold : FontWeights.SemiBold;
            if (action is not null)
            {
                _actions[id] = action;
                RefreshTrayMenu();
            }
        }

        private void RunWellAction(string id)
        {
            if (!_actions.TryGetValue(id, out var action) || string.IsNullOrWhiteSpace(action)) return;
            ExecuteActionCommand(action);
        }

        private void ExecuteActionCommand(string action)
        {
            try
            {
                if (action.Contains("://", StringComparison.OrdinalIgnoreCase))
                {
                    var psi = new ProcessStartInfo { FileName = action, UseShellExecute = true };
                    Process.Start(psi);
                }
                else
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = "/c " + action,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    Process.Start(psi);
                }
            }
            catch (Exception ex)
            {
                ShowTrayMessage("Action failed: " + ex.Message);
            }
        }

        private void ShowTrayMessage(string text)
        {
            try
            {
                _tray.BalloonTipTitle = "Traycer HUD";
                _tray.BalloonTipText = text;
                _tray.ShowBalloonTip(1500);
            }
            catch
            {
            }
        }

        private void RefreshTrayMenu()
        {
            if (_trayMenu == null) return;

            _trayMenu.SuspendLayout();
            _trayMenu.Items.Clear();

            _trayMenu.Items.Add(new WF.ToolStripMenuItem("Toggle Click-Through (Win+Alt+H)", null, (_, __) => ToggleClickThrough()));
            _trayMenu.Items.Add(BuildWellsMenu());
            _trayMenu.Items.Add(BuildTasksMenu());
            _trayMenu.Items.Add(new WF.ToolStripSeparator());
            _trayMenu.Items.Add(new WF.ToolStripMenuItem("Exit", null, (_, __) => Close()));

            _trayMenu.ResumeLayout();
        }

        private WF.ToolStripMenuItem BuildWellsMenu()
        {
            var wellsMenu = new WF.ToolStripMenuItem("Wells");
            if (_wells.Count == 0)
            {
                wellsMenu.Enabled = false;
                return wellsMenu;
            }

            foreach (var id in _wells.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
            {
                var sub = new WF.ToolStripMenuItem(id);
                var runItem = new WF.ToolStripMenuItem("Run action", null, (_, __) => RunWellAction(id))
                {
                    Enabled = _actions.TryGetValue(id, out var action) && !string.IsNullOrWhiteSpace(action)
                };
                var removeItem = new WF.ToolStripMenuItem("Remove", null, (_, __) => Dispatcher.Invoke(() => RemoveWell(id)));
                sub.DropDownItems.Add(runItem);
                sub.DropDownItems.Add(removeItem);
                wellsMenu.DropDownItems.Add(sub);
            }

            return wellsMenu;
        }

        private WF.ToolStripMenuItem BuildTasksMenu()
        {
            var tasksMenu = new WF.ToolStripMenuItem("Tasks");
            if (_tasks.Count == 0)
            {
                tasksMenu.Enabled = false;
                return tasksMenu;
            }

            foreach (var task in _tasks.Values.OrderBy(t => t.Config.Id, StringComparer.OrdinalIgnoreCase))
            {
                tasksMenu.DropDownItems.Add(BuildTaskMenu(task));
            }

            return tasksMenu;
        }

        private WF.ToolStripMenuItem BuildTaskMenu(ManagedTask task)
        {
            var label = task.Config.Id;
            if (task.IsScheduled)
            {
                label += " (scheduled)";
            }
            else if (task.ActiveProcess is { HasExited: false })
            {
                label += $" (running #{task.ActiveProcess.Id})";
            }

            var item = new WF.ToolStripMenuItem(label);
            if (task.IsScheduled)
            {
                item.DropDownItems.Add(new WF.ToolStripMenuItem("Run now", null, (_, __) => RunScheduledTask(task.Config)));
                item.DropDownItems.Add(new WF.ToolStripMenuItem("Stop", null, (_, __) => EndScheduledTask(task.Config)));
            }
            else
            {
                item.DropDownItems.Add(new WF.ToolStripMenuItem("Run now", null, (_, __) => StartTaskProcess(task, true)));
                var kill = new WF.ToolStripMenuItem("Kill process", null, (_, __) => KillTaskProcess(task))
                {
                    Enabled = task.ActiveProcess is { HasExited: false }
                };
                item.DropDownItems.Add(kill);
            }

            return item;
        }

        private void StartTaskProcess(ManagedTask task, bool fromMenu)
        {
            if (task.ActiveProcess is { HasExited: false })
            {
                if (fromMenu)
                {
                    ShowTrayMessage($"Task '{task.Config.Id}' is already running.");
                }
                return;
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = task.Config.Command,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                if (!string.IsNullOrWhiteSpace(task.Config.Arguments))
                {
                    psi.Arguments = task.Config.Arguments;
                }
                if (!string.IsNullOrWhiteSpace(task.Config.WorkingDirectory))
                {
                    psi.WorkingDirectory = task.Config.WorkingDirectory!;
                }
                var proc = Process.Start(psi);
                if (proc != null)
                {
                    proc.EnableRaisingEvents = true;
                    proc.Exited += (_, __) => Dispatcher.Invoke(() =>
                    {
                        task.ActiveProcess = null;
                        RefreshTrayMenu();
                    });
                    task.ActiveProcess = proc;
                }
            }
            catch (Exception ex)
            {
                ShowTrayMessage($"Task '{task.Config.Id}' failed: {ex.Message}");
            }
            finally
            {
                RefreshTrayMenu();
            }
        }

        private void KillTaskProcess(ManagedTask task)
        {
            if (task.ActiveProcess is { HasExited: false } proc)
            {
                try
                {
                    proc.Kill(true);
                }
                catch (Exception ex)
                {
                    ShowTrayMessage($"Unable to kill '{task.Config.Id}': {ex.Message}");
                }
            }

            task.ActiveProcess = null;
            RefreshTrayMenu();
        }

        private void RunScheduledTask(TaskConfig config)
        {
            var exit = RunSchtasks($"/Run /TN \"Traycer\\{config.Id}\"", out var stdout, out var stderr);
            if (exit != 0)
            {
                ShowTrayMessage($"Start '{config.Id}' failed: {FirstNonEmpty(stderr, stdout)}");
            }
        }

        private void EndScheduledTask(TaskConfig config)
        {
            var exit = RunSchtasks($"/End /TN \"Traycer\\{config.Id}\"", out var stdout, out var stderr);
            if (exit != 0)
            {
                ShowTrayMessage($"Stop '{config.Id}' failed: {FirstNonEmpty(stderr, stdout)}");
            }
        }

        private void EnsureScheduledTask(TaskConfig config)
        {
            if (config.Schedule is null)
            {
                ShowTrayMessage($"Task '{config.Id}' missing schedule configuration.");
                return;
            }

            var frequency = MapFrequency(config.Schedule.Frequency);
            if (frequency is null)
            {
                ShowTrayMessage($"Task '{config.Id}' has unsupported schedule frequency '{config.Schedule.Frequency}'.");
                return;
            }

            var commandLine = BuildCommandLine(config.Command, config.Arguments);
            var fullName = $"Traycer\\{config.Id}";

            RunSchtasks($"/Delete /TN \"{fullName}\" /F", out _, out _);

            var args = new System.Text.StringBuilder();
            args.Append($"/Create /F /TN \"{fullName}\" /TR \"{commandLine}\" /SC {frequency}");
            if (config.Schedule.Interval.HasValue && AllowsModifier(frequency))
            {
                var interval = Math.Max(1, config.Schedule.Interval.Value);
                args.Append($" /MO {interval}");
            }
            if (!string.IsNullOrWhiteSpace(config.Schedule.Start))
            {
                var start = config.Schedule.Start!.Trim();
                if (TimeSpan.TryParse(start, CultureInfo.InvariantCulture, out _))
                {
                    args.Append($" /ST {start}");
                }
            }
            args.Append(" /RL LIMITED");

            var runUser = GetCurrentUserPrincipal();
            if (!string.IsNullOrWhiteSpace(runUser))
            {
                args.Append($" /RU \"{runUser}\" /IT");
            }

            var exit = RunSchtasks(args.ToString(), out var stdout, out var stderr);
            if (exit != 0)
            {
                ShowTrayMessage($"Scheduling '{config.Id}' failed: {FirstNonEmpty(stderr, stdout)}");
                return;
            }

            if (config.AutoStart)
            {
                RunScheduledTask(config);
            }
        }

        private void RemoveScheduledTask(TaskConfig config)
        {
            RunSchtasks($"/Delete /TN \"Traycer\\{config.Id}\" /F", out _, out _);
        }

        private void ApplyTasks(IEnumerable<TaskConfig> configs)
        {
            var incoming = configs.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);

            foreach (var existing in _tasks.Values.ToList())
            {
                if (!incoming.ContainsKey(existing.Config.Id))
                {
                    if (!existing.IsScheduled && existing.ActiveProcess is { HasExited: false })
                    {
                        try { existing.ActiveProcess.Kill(true); } catch { }
                    }
                    if (existing.IsScheduled)
                    {
                        RemoveScheduledTask(existing.Config);
                    }
                    _tasks.Remove(existing.Config.Id);
                }
            }

            foreach (var cfg in incoming.Values)
            {
                if (!_tasks.TryGetValue(cfg.Id, out var managed))
                {
                    managed = new ManagedTask(cfg);
                    _tasks[cfg.Id] = managed;
                }
                else if (!managed.Config.Equals(cfg))
                {
                    if (managed.IsScheduled)
                    {
                        RemoveScheduledTask(managed.Config);
                    }
                    else if (managed.ActiveProcess is { HasExited: false })
                    {
                        try { managed.ActiveProcess.Kill(true); } catch { }
                        managed.ActiveProcess = null;
                    }
                    managed.Update(cfg);
                }
            }

            foreach (var task in _tasks.Values)
            {
                if (task.IsScheduled)
                {
                    EnsureScheduledTask(task.Config);
                }
                else if (task.Config.AutoStart)
                {
                    StartTaskProcess(task, false);
                }
            }

            RefreshTrayMenu();
        }

        private List<TaskConfig> ParseTaskConfigs(JsonElement tasksEl)
        {
            var list = new List<TaskConfig>();
            foreach (var element in tasksEl.EnumerateArray())
            {
                var cfg = ParseTaskConfig(element);
                if (cfg != null)
                {
                    list.Add(cfg);
                }
            }
            return list;
        }

        private TaskConfig? ParseTaskConfig(JsonElement element)
        {
            if (!element.TryGetProperty("id", out var idEl)) return null;
            var id = idEl.GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(id)) return null;

            if (!element.TryGetProperty("command", out var cmdEl)) return null;
            var command = cmdEl.GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(command)) return null;
            command = ResolveExecutablePath(command);

            string? arguments = element.TryGetProperty("args", out var argsEl) ? argsEl.GetString() : null;
            if (!string.IsNullOrWhiteSpace(arguments))
            {
                arguments = Environment.ExpandEnvironmentVariables(arguments);
            }
            string mode = element.TryGetProperty("mode", out var modeEl) ? modeEl.GetString() ?? "once" : "once";
            bool autoStart = true;
            if (element.TryGetProperty("autoStart", out var autoEl))
            {
                if (autoEl.ValueKind == JsonValueKind.True) autoStart = true;
                else if (autoEl.ValueKind == JsonValueKind.False) autoStart = false;
                else if (autoEl.ValueKind == JsonValueKind.String && bool.TryParse(autoEl.GetString(), out var parsed)) autoStart = parsed;
            }
            string? workingDirectory = element.TryGetProperty("workingDirectory", out var wdEl) ? wdEl.GetString() : null;
            workingDirectory = ResolveWorkingDirectory(workingDirectory);

            ScheduleConfig? schedule = null;
            if (string.Equals(mode, "schedule", StringComparison.OrdinalIgnoreCase) && element.TryGetProperty("schedule", out var schedEl) && schedEl.ValueKind == JsonValueKind.Object)
            {
                string frequency = schedEl.TryGetProperty("frequency", out var freqEl) ? freqEl.GetString() ?? string.Empty : string.Empty;
                int? interval = null;
                if (schedEl.TryGetProperty("interval", out var intEl))
                {
                    if (intEl.ValueKind == JsonValueKind.Number && intEl.TryGetInt32(out var val)) interval = val;
                    else if (intEl.ValueKind == JsonValueKind.String && int.TryParse(intEl.GetString(), out val)) interval = val;
                }
                string? start = schedEl.TryGetProperty("start", out var startEl) ? startEl.GetString() : null;
                schedule = new ScheduleConfig(frequency, interval, start);
            }

            return new TaskConfig(id, command, arguments, mode, autoStart, schedule, workingDirectory);
        }

        private static int RunSchtasks(string arguments, out string stdout, out string stderr)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                stdout = string.Empty;
                stderr = "Unable to start schtasks.exe";
                return -1;
            }

            stdout = proc.StandardOutput.ReadToEnd();
            stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            return proc.ExitCode;
        }

        private static string BuildCommandLine(string command, string? arguments)
        {
            var quoted = Quote(command);
            if (string.IsNullOrWhiteSpace(arguments)) return quoted;
            return $"{quoted} {arguments}";
        }

        private static string Quote(string value)
        {
            if (string.IsNullOrEmpty(value)) return "\"\"";
            if (!value.Any(char.IsWhiteSpace) && !value.Contains('"')) return value;
            var escaped = value.Replace("\"", "\\\"");
            return $"\"{escaped}\"";
        }

        private static bool AllowsModifier(string frequency)
            => frequency is "MINUTE" or "HOURLY" or "DAILY";

        private static string? MapFrequency(string? frequency)
        {
            if (string.IsNullOrWhiteSpace(frequency)) return null;
            return frequency.Trim().ToLowerInvariant() switch
            {
                "minute" or "minutes" => "MINUTE",
                "hour" or "hours" or "hourly" => "HOURLY",
                "day" or "days" or "daily" => "DAILY",
                "logon" or "onlogon" => "ONLOGON",
                "once" => "ONCE",
                _ => null
            };
        }

        private static string FirstNonEmpty(string? primary, string? fallback)
            => !string.IsNullOrWhiteSpace(primary) ? primary! : (!string.IsNullOrWhiteSpace(fallback) ? fallback! : string.Empty);

        private static string ResolveExecutablePath(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return command;
            var expanded = Environment.ExpandEnvironmentVariables(command);
            if (Path.IsPathRooted(expanded)) return Path.GetFullPath(expanded);

            var baseDirCandidate = Path.Combine(AppContext.BaseDirectory, expanded);
            if (File.Exists(baseDirCandidate)) return Path.GetFullPath(baseDirCandidate);

            var search = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrWhiteSpace(search))
            {
                foreach (var fragment in search.Split(Path.PathSeparator))
                {
                    if (string.IsNullOrWhiteSpace(fragment)) continue;
                    try
                    {
                        var candidate = Path.Combine(fragment.Trim(), expanded);
                        if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                        if (!expanded.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        {
                            var exeCandidate = candidate + ".exe";
                            if (File.Exists(exeCandidate)) return Path.GetFullPath(exeCandidate);
                        }
                    }
                    catch { }
                }
            }

            if (!expanded.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                var exeCandidate = expanded + ".exe";
                var exeInBase = Path.Combine(AppContext.BaseDirectory, exeCandidate);
                if (File.Exists(exeInBase)) return Path.GetFullPath(exeInBase);
            }

            return expanded;
        }

        private static string? ResolveWorkingDirectory(string? workingDirectory)
        {
            if (string.IsNullOrWhiteSpace(workingDirectory)) return null;
            var expanded = Environment.ExpandEnvironmentVariables(workingDirectory);
            if (!Path.IsPathRooted(expanded))
            {
                expanded = Path.Combine(AppContext.BaseDirectory, expanded);
            }
            try { return Path.GetFullPath(expanded); }
            catch { return workingDirectory; }
        }

        private static string? GetCurrentUserPrincipal()
        {
            try
            {
                var user = Environment.UserName;
                if (string.IsNullOrWhiteSpace(user)) return null;
                var domain = Environment.UserDomainName;
                if (!string.IsNullOrWhiteSpace(domain) && !string.Equals(domain, user, StringComparison.OrdinalIgnoreCase))
                {
                    return $"{domain}\\{user}";
                }
                return user;
            }
            catch
            {
                return null;
            }
        }
        private static bool TryParseColor(string hex, out System.Windows.Media.Color color)
        {
            try
            {
                if (hex.StartsWith("#")) hex = hex[1..];
                if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex[2..];
                if (hex.StartsWith("hex:", StringComparison.OrdinalIgnoreCase)) hex = hex[4..];

                if (hex.Length == 6)
                {
                    byte r = Convert.ToByte(hex[0..2], 16);
                    byte g = Convert.ToByte(hex[2..4], 16);
                    byte b = Convert.ToByte(hex[4..6], 16);
                    color = System.Windows.Media.Color.FromRgb(r, g, b);
                    return true;
                }
                if (hex.Length == 8)
                {
                    byte a = Convert.ToByte(hex[0..2], 16);
                    byte r = Convert.ToByte(hex[2..4], 16);
                    byte g = Convert.ToByte(hex[4..6], 16);
                    byte b = Convert.ToByte(hex[6..8], 16);
                    color = System.Windows.Media.Color.FromArgb(a, r, g, b);
                    return true;
                }
            }
            catch { }
            color = default;
            return false;
        }

        // ===== IPC =====
        private const string PIPE_NAME = "TraycerHud";

        private async Task PipeLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PIPE_NAME, PipeDirection.InOut, 1,
                                                                 PipeTransmissionMode.Message, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(_cts.Token);

                    using var reader = new StreamReader(server, new UTF8Encoding(false));
                    string? line;
                    while ((line = await reader.ReadLineAsync()) != null && !_cts.IsCancellationRequested)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        try { using var doc = JsonDocument.Parse(line); HandleMessage(doc.RootElement); }
                        catch { /* ignore malformed lines */ }
                    }
                }
                catch when (_cts.IsCancellationRequested) { }
                catch { await Task.Delay(200); }
            }
        }

        private void HandleMessage(JsonElement msg)
        {
            string op = msg.TryGetProperty("op", out var opEl) ? opEl.GetString() ?? "" : "";

            if (op.Equals("config", StringComparison.OrdinalIgnoreCase))
            {
                List<WellConfig>? wells = null;
                if (msg.TryGetProperty("wells", out var wellsEl) && wellsEl.ValueKind == JsonValueKind.Array)
                {
                    wells = new List<WellConfig>();
                    foreach (var w in wellsEl.EnumerateArray())
                    {
                        var id = w.GetProperty("id").GetString() ?? "";
                        var width = w.TryGetProperty("width", out var widEl) ? widEl.GetDouble() : 200.0;
                        wells.Add(new WellConfig(id, width));
                    }
                }

                List<TaskConfig>? tasks = null;
                if (msg.TryGetProperty("tasks", out var tasksEl) && tasksEl.ValueKind == JsonValueKind.Array)
                {
                    tasks = ParseTaskConfigs(tasksEl);
                }

                if (wells != null || tasks != null)
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (wells != null)
                        {
                            ApplyConfig(wells);
                            ApplyChrome();
                        }
                        if (tasks != null)
                        {
                            ApplyTasks(tasks);
                        }
                    });
                }
            }
            else if (op.Equals("add", StringComparison.OrdinalIgnoreCase))
            {
                string id = msg.GetProperty("well").GetString() ?? "";
                double width = msg.TryGetProperty("width", out var wEl) ? wEl.GetDouble() : 200.0;
                int? index = msg.TryGetProperty("index", out var iEl) ? iEl.GetInt32() : (int?)null;
                Dispatcher.Invoke(() => { AddWell(id, width, index); ReassertTopmost(); });
            }
            else if (op.Equals("remove", StringComparison.OrdinalIgnoreCase))
            {
                string id = msg.GetProperty("well").GetString() ?? "";
                Dispatcher.Invoke(() => { RemoveWell(id); ReassertTopmost(); });
            }
            else if (op.Equals("resize", StringComparison.OrdinalIgnoreCase))
            {
                string id = msg.GetProperty("well").GetString() ?? "";
                double width = msg.GetProperty("width").GetDouble();
                Dispatcher.Invoke(() => { ResizeWell(id, width); });
            }
            else if (op.Equals("set", StringComparison.OrdinalIgnoreCase))
            {
                string id = msg.GetProperty("well").GetString() ?? "";
                string? text = msg.TryGetProperty("text", out var tEl) ? tEl.GetString() : null;
                string? fg = msg.TryGetProperty("fg", out var fgEl) ? fgEl.GetString() : null;
                string? bg = msg.TryGetProperty("bg", out var bgEl) ? bgEl.GetString() : null;
                bool? blink = msg.TryGetProperty("blink", out var bEl) ? bEl.GetBoolean() : null;
                string? action = msg.TryGetProperty("action", out var aEl) ? aEl.GetString() : null;
                Dispatcher.Invoke(() => SetWell(id, text, fg, bg, blink, action));
            }
            else if (op.Equals("bulk", StringComparison.OrdinalIgnoreCase))
            {
                if (msg.TryGetProperty("updates", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    Dispatcher.Invoke(() =>
                    {
                        foreach (var u in arr.EnumerateArray())
                        {
                            string id = u.GetProperty("well").GetString() ?? "";
                            string? text = u.TryGetProperty("text", out var tEl) ? tEl.GetString() : null;
                            string? fg = u.TryGetProperty("fg", out var fgEl) ? fgEl.GetString() : null;
                            string? bg = u.TryGetProperty("bg", out var bgEl) ? bgEl.GetString() : null;
                            bool? blink = u.TryGetProperty("blink", out var bEl) ? bEl.GetBoolean() : null;
                            string? action = u.TryGetProperty("action", out var aEl) ? aEl.GetString() : null;
                            SetWell(id, text, fg, bg, blink, action);
                        }
                    });
                }
            }
            else if (op.Equals("bind", StringComparison.OrdinalIgnoreCase))
            {
                string id = msg.GetProperty("well").GetString() ?? "";
                string action = msg.GetProperty("action").GetString() ?? "";
                Dispatcher.Invoke(() =>
                {
                    _actions[id] = action;
                    RefreshTrayMenu();
                });
            }
            else if (op.Equals("placement", StringComparison.OrdinalIgnoreCase))
            {
                if (msg.TryGetProperty("height", out var hEl)) _heightOverride = hEl.GetDouble();
                if (msg.TryGetProperty("bottomOffset", out var bEl)) _bottomOffset = bEl.GetDouble();
                if (msg.TryGetProperty("padding", out var pEl)) _padding = pEl.GetDouble();
                if (msg.TryGetProperty("cornerRadius", out var cEl)) _cornerRadius = cEl.GetDouble();

                Dispatcher.Invoke(() =>
                {
                    PositionOverTaskbarLeftThird();
                    ApplyChrome();
                    ReassertTopmost();
                });
            }
        }

        // ===== Hotkey =====
        private void ToggleClickThrough()
        {
            _clickThrough = !_clickThrough;
            int ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
            ex |= WS_EX_TOOLWINDOW | WS_EX_LAYERED;
            if (_clickThrough) ex |= WS_EX_TRANSPARENT;
            else ex &= ~WS_EX_TRANSPARENT;
            SetWindowLong(_hwnd, GWL_EXSTYLE, ex);

            _tray.BalloonTipTitle = "Traycer HUD";
            _tray.BalloonTipText = _clickThrough ? "Click-through: ON" : "Click-through: OFF (click wells to run actions)";
            _tray.ShowBalloonTip(1200);

            ReassertTopmost();
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_HOTKEY = 0x0312;
            if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
            {
                ToggleClickThrough();
                handled = true;
            }
            return IntPtr.Zero;
        }

        // === Default config ===
        private const string DEFAULTS_FILE = "traycer.defaults.json";
        private bool TryLoadDefaults(out string? error)
        {
            error = null;
            try
            {
                string? path = FindDefaultsFile();
                if (path is null)
                {
                    error = "Traycer defaults file not found.";
                    return false;
                }

                var json = File.ReadAllText(path, Encoding.UTF8);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                List<TaskConfig>? tasks = null;

                // 1) placement
                if (root.TryGetProperty("placement", out var p) && p.ValueKind == JsonValueKind.Object)
                {
                    if (p.TryGetProperty("height", out var hEl)) _heightOverride = hEl.GetDouble();
                    if (p.TryGetProperty("bottomOffset", out var bEl)) _bottomOffset = bEl.GetDouble();
                    if (p.TryGetProperty("padding", out var padEl)) _padding = padEl.GetDouble();
                    if (p.TryGetProperty("cornerRadius", out var crEl)) _cornerRadius = crEl.GetDouble();
                }

                // 2) wells (supports optional "index"; otherwise preserves file order)
                if (root.TryGetProperty("wells", out var wellsEl) && wellsEl.ValueKind == JsonValueKind.Array)
                {
                    var tmp = new List<(int? index, WellConfig cfg)>();
                    int pos = 0;
                    foreach (var w in wellsEl.EnumerateArray())
                    {
                        string id = w.GetProperty("id").GetString() ?? $"well{pos}";
                        double width = w.TryGetProperty("width", out var widEl) ? widEl.GetDouble() : 200.0;
                        int? index = w.TryGetProperty("index", out var iEl) ? iEl.GetInt32() : (int?)null;
                        tmp.Add((index, new WellConfig(id, width)));
                        pos++;
                    }

                    IEnumerable<WellConfig> ordered = tmp.Any(t => t.index.HasValue)
                        ? tmp.OrderBy(t => t.index ?? int.MaxValue).Select(t => t.cfg)
                        : tmp.Select(t => t.cfg);

                    ApplyConfig(ordered);
                }

                // 3) optional seed updates (text/colors/actions)
                if (root.TryGetProperty("updates", out var updatesEl) && updatesEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var u in updatesEl.EnumerateArray())
                    {
                        string id = u.GetProperty("well").GetString() ?? "";
                        string? text = u.TryGetProperty("text", out var tEl) ? tEl.GetString() : null;
                        string? fg = u.TryGetProperty("fg", out var fgEl) ? fgEl.GetString() : null;
                        string? bg = u.TryGetProperty("bg", out var bgEl) ? bgEl.GetString() : null;
                        bool? blink = u.TryGetProperty("blink", out var bEl) ? bEl.GetBoolean() : null;
                        string? action = u.TryGetProperty("action", out var aEl) ? aEl.GetString() : null;
                        SetWell(id, text, fg, bg, blink, action);
                    }
                }

                // 4) optional background tasks
                if (root.TryGetProperty("tasks", out var tasksEl) && tasksEl.ValueKind == JsonValueKind.Array)
                {
                    tasks = ParseTaskConfigs(tasksEl);
                }

                // 5) optional standalone actions map: { "actions": { "wellId": "cmd or url", ... } }
                if (root.TryGetProperty("actions", out var actsEl) && actsEl.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in actsEl.EnumerateObject())
                    {
                        _actions[prop.Name] = prop.Value.GetString() ?? "";
                    }
                }

                ApplyTasks(tasks ?? Enumerable.Empty<TaskConfig>());
                ApplyChrome();
                RefreshTrayMenu();
                return true;
            }
            catch (Exception ex)
            {
                error = $"Failed to load defaults: {ex.Message}";
                return false;
            }
        }


        private static string? FindDefaultsFile()
        {
            try
            {
                // 1) beside the EXE
                var exeDir = AppContext.BaseDirectory;
                var p1 = Path.Combine(exeDir, DEFAULTS_FILE);
                if (File.Exists(p1)) return p1;

                // 2) %LOCALAPPDATA%\Traycer\defaults.json
                var p2 = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Traycer", "defaults.json");
                if (File.Exists(p2)) return p2;
            }
            catch { }
            return null;
        }

    }
}
