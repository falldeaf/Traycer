using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using WF = System.Windows.Forms;
using SD = System.Drawing;

namespace Traycer
{
    public partial class MainWindow : Window
    {
        private readonly SD.Icon? _trayIcon;
        private readonly WF.NotifyIcon _tray;
        private bool _clickThrough;
        private IntPtr _hwnd;

        private readonly CancellationTokenSource _cts = new();
        private System.Threading.Tasks.Task? _pipeTask;

        private readonly Dictionary<string, (Border border, TextBlock text)> _wells = new();
        private readonly Dictionary<string, string> _actions = new();
        private readonly Dictionary<string, ManagedTask> _tasks = new(StringComparer.OrdinalIgnoreCase);
        private readonly WF.ContextMenuStrip _trayMenu;

        private double? _heightOverride = 26;
        private double _bottomOffset = 2;
        private double _padding = 6;
        private double _cornerRadius = 8;

        private System.Windows.Threading.DispatcherTimer? _topmostTimer;

        public MainWindow()
        {
            InitializeComponent();

            if (!TryEnsureDefaultsFile(out var defaultsPath) || string.IsNullOrWhiteSpace(defaultsPath))
            {
                Environment.Exit(1);
                return;
            }

            if (!TryLoadDefaults(defaultsPath, out var error))
            {
                var message = string.IsNullOrWhiteSpace(error) ? "Traycer could not load its defaults file." : error;
                try
                {
                    Console.Error.WriteLine(message);
                }
                catch
                {
                }

                System.Windows.MessageBox.Show(message, "Traycer", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
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

            _trayIcon = LoadTrayIcon();
            _tray = new WF.NotifyIcon
            {
                Icon = _trayIcon ?? SD.SystemIcons.Information,
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
            if (_clickThrough)
            {
                ex |= WS_EX_TRANSPARENT;
            }

            SetWindowLong(_hwnd, GWL_EXSTYLE, ex);
            RegisterHotKey(_hwnd, HOTKEY_ID, MOD_WIN | MOD_ALT, VK_H);

            PositionOverTaskbarLeftThird();
            ReassertTopmost();

            _topmostTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            _topmostTimer.Tick += (_, __) => ReassertTopmost();
            _topmostTimer.Start();

            _pipeTask = System.Threading.Tasks.Task.Run(PipeLoopAsync);
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            try
            {
                UnregisterHotKey(_hwnd, HOTKEY_ID);
            }
            catch
            {
            }

            _cts.Cancel();
            try
            {
                _pipeTask?.Wait(500);
            }
            catch
            {
            }

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
                catch
                {
                }
            }

            _tasks.Clear();

            _tray.Visible = false;
            _tray.Dispose();
            _trayIcon?.Dispose();
        }
    }
}
