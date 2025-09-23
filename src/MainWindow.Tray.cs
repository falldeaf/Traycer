using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using WF = System.Windows.Forms;
using SD = System.Drawing;

namespace Traycer
{
    public partial class MainWindow
    {
        /// <summary>
        /// Loads the tray icon from resources.
        /// </summary>
        /// <returns>Icon or null.</returns>
        private static SD.Icon? LoadTrayIcon()
        {
            try
            {
                var uri = new Uri("pack://application:,,,/images/Traycer.ico");
                var streamInfo = System.Windows.Application.GetResourceStream(uri);
                if (streamInfo is null)
                {
                    return null;
                }

                using var stream = streamInfo.Stream;
                return new SD.Icon(stream);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Traycer tray icon load failed: {ex}");
                return null;
            }
        }

        /// <summary>
        /// Rebuilds the tray context menu.
        /// </summary>
        private void RefreshTrayMenu()
        {
            if (_trayMenu == null)
            {
                return;
            }

            _trayMenu.SuspendLayout();
            _trayMenu.Items.Clear();

            _trayMenu.Items.Add(new WF.ToolStripMenuItem("Toggle Click-Through (Win+Alt+H)", null, (_, __) => ToggleClickThrough()));
            _trayMenu.Items.Add(BuildWellsMenu());
            _trayMenu.Items.Add(BuildTasksMenu());
            _trayMenu.Items.Add(new WF.ToolStripSeparator());
            _trayMenu.Items.Add(new WF.ToolStripMenuItem("Exit", null, (_, __) => Close()));

            _trayMenu.ResumeLayout();
        }

        /// <summary>
        /// Builds the wells submenu.
        /// </summary>
        /// <returns>Menu item.</returns>
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

        /// <summary>
        /// Builds the tasks submenu.
        /// </summary>
        /// <returns>Menu item.</returns>
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

        /// <summary>
        /// Crafts a submenu for a single task.
        /// </summary>
        /// <param name="task">Target task.</param>
        /// <returns>Menu item.</returns>
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

        /// <summary>
        /// Displays a brief tray balloon.
        /// </summary>
        /// <param name="text">Message text.</param>
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
    }
}

