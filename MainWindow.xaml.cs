// MainWindow.xaml.cs
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
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
            if (!TryLoadDefaults())
            {
                ApplyConfig(new[]
                {
                    new WellConfig("weather", 240),
                    new WellConfig("build",   180),
                });
            }

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
            var menu = new WF.ContextMenuStrip();
            menu.Items.Add("Toggle Click-Through (Win+Alt+H)", null, (_, __) => ToggleClickThrough());
            menu.Items.Add(new WF.ToolStripSeparator());
            menu.Items.Add("Exit", null, (_, __) => Close());
            _tray.ContextMenuStrip = menu;
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
            _tray.Visible = false;
            _tray.Dispose();
        }

        // ===== Layout / placement =====
        private void PositionOverTaskbarLeftThird()
        {
            var (_, rectTB) = GetTaskbarRect();
            var screen = WF.Screen.PrimaryScreen!.Bounds;

            double width = Math.Max(80, screen.Width / 3.0) - (2 * _padding);
            double height = (_heightOverride ?? rectTB.Height - (2 * _padding));
            height = Math.Max(20, height);

            double x = screen.Left + _padding;
            double y = screen.Bottom - height - _bottomOffset - _padding;

            Left = x; Top = y; Width = width; Height = height;
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
        }

        private void ResizeWell(string id, double width)
        {
            int? col = GetColumnOf(id);
            if (col is null) return;
            WellsGrid.ColumnDefinitions[col.Value].Width = new GridLength(width);
        }

        private void OnWellClick(object sender, MouseButtonEventArgs e)
        {
            if (_clickThrough) return;
            if (sender is not Border b || b.Tag is not string id) return;
            if (!_actions.TryGetValue(id, out var action) || string.IsNullOrWhiteSpace(action)) return;

            try
            {
                if (action.Contains("://"))
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
                _tray.BalloonTipTitle = "Traycer HUD";
                _tray.BalloonTipText = "Action failed: " + ex.Message;
                _tray.ShowBalloonTip(1500);
            }
        }

        private void SetWell(string id, string? text = null, string? fg = null, string? bg = null, bool? blink = null, string? action = null)
        {
            if (!_wells.TryGetValue(id, out var tuple)) return;
            var (border, tb) = tuple;

            if (text is not null) tb.Text = text;
            if (fg is not null && TryParseColor(fg, out var c)) tb.Foreground = new SolidColorBrush(c);
            if (bg is not null && TryParseColor(bg, out var c2)) border.Background = new SolidColorBrush(c2);
            if (blink is not null) tb.FontWeight = blink.Value ? FontWeights.Bold : FontWeights.SemiBold;
            if (action is not null) _actions[id] = action;
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
                if (msg.TryGetProperty("wells", out var wellsEl) && wellsEl.ValueKind == JsonValueKind.Array)
                {
                    var list = new List<WellConfig>();
                    foreach (var w in wellsEl.EnumerateArray())
                    {
                        var id = w.GetProperty("id").GetString() ?? "";
                        var width = w.TryGetProperty("width", out var widEl) ? widEl.GetDouble() : 200.0;
                        list.Add(new WellConfig(id, width));
                    }
                    Dispatcher.Invoke(() => { ApplyConfig(list); ApplyChrome(); });
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
                Dispatcher.Invoke(() => { _actions[id] = action; });
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
        private bool TryLoadDefaults()
        {
            try
            {
                string? path = FindDefaultsFile();
                if (path is null) return false;

                var json = File.ReadAllText(path, Encoding.UTF8);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

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

                // 4) optional standalone actions map: { "actions": { "wellId": "cmd or url", ... } }
                if (root.TryGetProperty("actions", out var actsEl) && actsEl.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in actsEl.EnumerateObject())
                    {
                        _actions[prop.Name] = prop.Value.GetString() ?? "";
                    }
                }

                ApplyChrome();
                return true;
            }
            catch
            {
                // If the file is present but invalid, fail soft and continue with built-ins
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
