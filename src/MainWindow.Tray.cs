using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using WF = System.Windows.Forms;
using SD = System.Drawing;
using Traycer.Update;

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

            _trayMenu.Items.Add(BuildUpdatesMenu());
            _trayMenu.Items.Add(new WF.ToolStripSeparator());
            _trayMenu.Items.Add(new WF.ToolStripMenuItem("Toggle Click-Through (Win+Alt+H)", null, (_, __) => ToggleClickThrough()));
            _trayMenu.Items.Add(BuildWellsMenu());
            _trayMenu.Items.Add(BuildTasksMenu());
            _trayMenu.Items.Add(new WF.ToolStripSeparator());
            _trayMenu.Items.Add(new WF.ToolStripMenuItem("Exit", null, (_, __) => Close()));

            _trayMenu.ResumeLayout();
        }

        private WF.ToolStripMenuItem BuildUpdatesMenu()
        {
            var updatesMenu = new WF.ToolStripMenuItem("Updates");
            var checking = _isCheckingForUpdates;
            var updating = _isUpdating;
            var update = _availableUpdate;

            var checkLabel = checking ? "Checking..." : "Check for Updates...";
            var checkItem = new WF.ToolStripMenuItem(checkLabel, null, OnCheckForUpdatesClicked)
            {
                Enabled = !checking && !updating
            };
            updatesMenu.DropDownItems.Add(checkItem);

            var wingetItem = new WF.ToolStripMenuItem("Update via winget", null, OnWingetUpdateClicked)
            {
                Enabled = update is not null && !checking && !updating
            };
            updatesMenu.DropDownItems.Add(wingetItem);

            if (update is UpdateAvailability available)
            {
                var installerItem = new WF.ToolStripMenuItem($"Install {available.DisplayVersion}...", null, OnInstallerUpdateClicked)
                {
                    Enabled = !checking && !updating
                };
                updatesMenu.DropDownItems.Add(installerItem);

                var statusItem = new WF.ToolStripMenuItem($"Latest {available.DisplayVersion}")
                {
                    Enabled = false
                };
                updatesMenu.DropDownItems.Add(new WF.ToolStripSeparator());
                updatesMenu.DropDownItems.Add(statusItem);
            }
            else
            {
                var installerItem = new WF.ToolStripMenuItem("Install latest...", null, OnInstallerUpdateClicked)
                {
                    Enabled = false
                };
                updatesMenu.DropDownItems.Add(installerItem);

                var statusItem = new WF.ToolStripMenuItem($"Current v{AppVersion.NormalizedVersion}")
                {
                    Enabled = false
                };
                updatesMenu.DropDownItems.Add(new WF.ToolStripSeparator());
                updatesMenu.DropDownItems.Add(statusItem);
            }

            return updatesMenu;
        }

        private async void OnCheckForUpdatesClicked(object? sender, EventArgs e)
        {
            await CheckForUpdatesAsync(true, true);
        }

        private async void OnWingetUpdateClicked(object? sender, EventArgs e)
        {
            var update = _availableUpdate;
            if (update is null || _isUpdating)
            {
                return;
            }

            _isUpdating = true;
            RefreshTrayMenu();

            try
            {
                bool wingetStarted;
                try
                {
                    wingetStarted = await _updateService.TryLaunchWingetUpgradeAsync(update, _cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Winget update launch failed: {ex}");
                    wingetStarted = false;
                }

                if (wingetStarted)
                {
                    ShowTrayMessage("winget update started. Traycer will exit.");
                    Close();
                    return;
                }

                ShowTrayMessage("winget not available, downloading installer...");

                bool installerStarted;
                try
                {
                    installerStarted = await _updateService.TryLaunchInstallerUpgradeAsync(update, _cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Installer update launch failed: {ex}");
                    installerStarted = false;
                }

                if (installerStarted)
                {
                    ShowTrayMessage($"Installer for {update.DisplayVersion} started. Traycer will exit.");
                    Close();
                }
                else
                {
                    ShowTrayMessage("Failed to start installer update.");
                }
            }
            finally
            {
                _isUpdating = false;
                RefreshTrayMenu();
            }
        }

        private async void OnInstallerUpdateClicked(object? sender, EventArgs e)
        {
            var update = _availableUpdate;
            if (update is null || _isUpdating)
            {
                return;
            }

            _isUpdating = true;
            RefreshTrayMenu();

            try
            {
                bool installerStarted;
                try
                {
                    installerStarted = await _updateService.TryLaunchInstallerUpgradeAsync(update, _cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Installer update launch failed: {ex}");
                    installerStarted = false;
                }

                if (installerStarted)
                {
                    ShowTrayMessage($"Installer for {update.DisplayVersion} started. Traycer will exit.");
                    Close();
                }
                else
                {
                    ShowTrayMessage("Failed to start installer update.");
                }
            }
            finally
            {
                _isUpdating = false;
                RefreshTrayMenu();
            }
        }

        private async Task<UpdateAvailability?> CheckForUpdatesAsync(bool force, bool showNotification)
        {
            if (_isCheckingForUpdates)
            {
                return _availableUpdate;
            }

            try
            {
                _isCheckingForUpdates = true;
                RefreshTrayMenu();

                if (force)
                {
                    await _updateService.ForceCheckAsync(_cts.Token);
                }
                else
                {
                    await _updateService.CheckIfDueAsync(_cts.Token);
                }

                _availableUpdate = _updateService.LatestUpdate;
                UpdateTrayIconForUpdates();

                if (showNotification)
                {
                    if (_availableUpdate is null)
                    {
                        ShowTrayMessage("Traycer is up to date.");
                    }
                    else
                    {
                        ShowTrayMessage($"Traycer {_availableUpdate.DisplayVersion} is ready to install.");
                    }
                }

                return _availableUpdate;
            }
            catch (OperationCanceledException)
            {
                return _availableUpdate;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Traycer update check failed: {ex}");
                if (showNotification)
                {
                    ShowTrayMessage("Update check failed. See logs for details.");
                }

                return _availableUpdate;
            }
            finally
            {
                _isCheckingForUpdates = false;
                RefreshTrayMenu();
            }
        }

        private void OnUpdateAvailabilityChanged(object? sender, UpdateAvailabilityChangedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                var hadUpdate = _availableUpdate is not null;
                _availableUpdate = e.Current;
                UpdateTrayIconForUpdates();
                RefreshTrayMenu();

                if (!hadUpdate && _availableUpdate is not null)
                {
                    ShowTrayMessage($"Traycer {_availableUpdate.DisplayVersion} is available.");
                }
            });
        }

        private void UpdateTrayIconForUpdates()
        {
            if (_tray == null)
            {
                return;
            }

            if (_availableUpdate is not null)
            {
                EnsureUpdateBadgeIcon();
                if (_updateBadgeIcon is not null)
                {
                    _tray.Icon = _updateBadgeIcon;
                }

                _tray.Text = $"{AppConstants.AppDisplayName} v{AppVersion.NormalizedVersion} (update available)";
            }
            else
            {
                _tray.Icon = _trayIcon ?? SD.SystemIcons.Information;
                _tray.Text = $"{AppConstants.AppDisplayName} v{AppVersion.NormalizedVersion}";
            }
        }

        private void EnsureUpdateBadgeIcon()
        {
            if (_updateBadgeIcon != null || _trayIcon == null)
            {
                return;
            }

            try
            {
                using var baseBitmap = _trayIcon.ToBitmap();
                using var canvas = new SD.Bitmap(baseBitmap.Width, baseBitmap.Height, SD.Imaging.PixelFormat.Format32bppArgb);
                using (var graphics = SD.Graphics.FromImage(canvas))
                {
                    graphics.DrawImage(baseBitmap, 0, 0, baseBitmap.Width, baseBitmap.Height);
                    var dotSize = Math.Max(6, baseBitmap.Width / 3);
                    var dotRect = new SD.Rectangle(baseBitmap.Width - dotSize, 0, dotSize, dotSize);
                    using var brush = new SD.SolidBrush(SD.Color.FromArgb(220, 204, 46, 46));
                    graphics.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
                    graphics.FillEllipse(brush, dotRect);
                }

                var handle = canvas.GetHicon();
                var iconFromHandle = SD.Icon.FromHandle(handle);
                _updateBadgeIcon = (SD.Icon)iconFromHandle.Clone();
                DestroyIcon(handle);
                iconFromHandle.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Traycer update badge creation failed: {ex}");
            }
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





