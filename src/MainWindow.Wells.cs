using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Traycer
{
    public partial class MainWindow
    {
        /// <summary>
        /// Captures persisted well layout.
        /// </summary>
        private record WellConfig(string id, double width);

        /// <summary>
        /// Rebuilds the wells grid for supplied configs.
        /// </summary>
        /// <param name="configs">Well definitions.</param>
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

        /// <summary>
        /// Creates the border/text pair for a well.
        /// </summary>
        /// <param name="id">Well id.</param>
        /// <returns>Border and text refs.</returns>
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

        /// <summary>
        /// Locates the column index for a well id.
        /// </summary>
        /// <param name="id">Well id.</param>
        /// <returns>Column index or null.</returns>
        private int? GetColumnOf(string id)
        {
            if (!_wells.TryGetValue(id, out var tuple))
            {
                return null;
            }

            return Grid.GetColumn(tuple.border);
        }

        /// <summary>
        /// Inserts a new well at the requested slot.
        /// </summary>
        /// <param name="id">Well id.</param>
        /// <param name="width">Column width.</param>
        /// <param name="index">Optional index.</param>
        private void AddWell(string id, double width, int? index = null)
        {
            if (_wells.ContainsKey(id))
            {
                ResizeWell(id, width);
                return;
            }

            int insert = Math.Clamp(index ?? WellsGrid.ColumnDefinitions.Count, 0, WellsGrid.ColumnDefinitions.Count);
            WellsGrid.ColumnDefinitions.Insert(insert, new ColumnDefinition { Width = new GridLength(width) });

            foreach (var child in WellsGrid.Children.OfType<Border>())
            {
                int c = Grid.GetColumn(child);
                if (c >= insert)
                {
                    Grid.SetColumn(child, c + 1);
                }
            }

            var (border, tb) = CreateWellVisual(id);
            Grid.SetColumn(border, insert);
            WellsGrid.Children.Add(border);
            _wells[id] = (border, tb);
            ApplyChrome();
            RefreshTrayMenu();
        }

        /// <summary>
        /// Removes a well column and related action.
        /// </summary>
        /// <param name="id">Well id.</param>
        private void RemoveWell(string id)
        {
            if (!_wells.TryGetValue(id, out var tuple))
            {
                return;
            }

            int col = Grid.GetColumn(tuple.border);
            WellsGrid.Children.Remove(tuple.border);
            _wells.Remove(id);
            _actions.Remove(id);

            WellsGrid.ColumnDefinitions.RemoveAt(col);
            foreach (var child in WellsGrid.Children.OfType<Border>())
            {
                int c = Grid.GetColumn(child);
                if (c > col)
                {
                    Grid.SetColumn(child, c - 1);
                }
            }

            ApplyChrome();
            RefreshTrayMenu();
        }

        /// <summary>
        /// Adjusts an existing well width.
        /// </summary>
        /// <param name="id">Well id.</param>
        /// <param name="width">Requested width.</param>
        private void ResizeWell(string id, double width)
        {
            int? col = GetColumnOf(id);
            if (col is null)
            {
                return;
            }

            WellsGrid.ColumnDefinitions[col.Value].Width = new GridLength(width);
            UpdateWindowWidth();
        }

        /// <summary>
        /// Handles well clicks to launch actions.
        /// </summary>
        /// <param name="sender">Border source.</param>
        /// <param name="e">Click args.</param>
        private void OnWellClick(object sender, MouseButtonEventArgs e)
        {
            if (_clickThrough)
            {
                return;
            }

            if (sender is not Border b || b.Tag is not string id)
            {
                return;
            }

            if (!_actions.TryGetValue(id, out var action) || string.IsNullOrWhiteSpace(action))
            {
                return;
            }

            ExecuteActionCommand(action);
        }

        /// <summary>
        /// Updates visual and action state for a well.
        /// </summary>
        /// <param name="id">Well id.</param>
        /// <param name="text">New label.</param>
        /// <param name="fg">Foreground hex.</param>
        /// <param name="bg">Background hex.</param>
        /// <param name="blink">Blink flag.</param>
        /// <param name="action">Action command.</param>
        private void SetWell(string id, string? text = null, string? fg = null, string? bg = null, bool? blink = null, string? action = null)
        {
            if (!_wells.TryGetValue(id, out var tuple))
            {
                return;
            }

            var (border, tb) = tuple;

            if (text is not null)
            {
                tb.Text = text;
            }

            if (fg is not null && TryParseColor(fg, out var c))
            {
                tb.Foreground = new SolidColorBrush(c);
            }

            if (bg is not null && TryParseColor(bg, out var c2))
            {
                border.Background = new SolidColorBrush(c2);
            }

            if (blink is not null)
            {
                tb.FontWeight = blink.Value ? FontWeights.Bold : FontWeights.SemiBold;
            }

            if (action is not null)
            {
                _actions[id] = action;
                RefreshTrayMenu();
            }
        }

        /// <summary>
        /// Runs the configured command for a well.
        /// </summary>
        /// <param name="id">Well id.</param>
        private void RunWellAction(string id)
        {
            if (!_actions.TryGetValue(id, out var action) || string.IsNullOrWhiteSpace(action))
            {
                return;
            }

            ExecuteActionCommand(action);
        }

        /// <summary>
        /// Executes a command or shell verb.
        /// </summary>
        /// <param name="action">Action text.</param>
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
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    };
                    Process.Start(psi);
                }
            }
            catch (Exception ex)
            {
                ShowTrayMessage("Action failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Attempts to parse a hex color.
        /// </summary>
        /// <param name="hex">Hex literal.</param>
        /// <param name="color">Parsed color.</param>
        /// <returns>True when parsed.</returns>
        private static bool TryParseColor(string hex, out System.Windows.Media.Color color)
        {
            try
            {
                if (hex.StartsWith("#"))
                {
                    hex = hex[1..];
                }

                if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    hex = hex[2..];
                }

                if (hex.StartsWith("hex:", StringComparison.OrdinalIgnoreCase))
                {
                    hex = hex[4..];
                }

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
            catch
            {
            }

            color = default;
            return false;
        }
    }
}


