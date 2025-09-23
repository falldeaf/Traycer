using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WF = System.Windows.Forms;

namespace Traycer
{
    public partial class MainWindow
    {
        /// <summary>
        /// Aligns the HUD above the taskbar left third.
        /// </summary>
        private void PositionOverTaskbarLeftThird()
        {
            var (_, rectTB) = GetTaskbarRect();
            var screen = WF.Screen.PrimaryScreen!.Bounds;

            double height = _heightOverride ?? rectTB.Height - (2 * _padding);
            height = Math.Max(20, height);

            double x = screen.Left + _padding;
            double y = screen.Bottom - height - _bottomOffset - _padding;

            Left = x;
            Top = y;
            Height = height;
            ApplyChrome();
        }

        /// <summary>
        /// Applies padding and radii styling to chrome.
        /// </summary>
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

        /// <summary>
        /// Computes width to wrap current well layout.
        /// </summary>
        private void UpdateWindowWidth()
        {
            if (RootBorder == null || WellsGrid == null)
            {
                return;
            }

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

            Width = Math.Max(80, totalWidth);
        }
    }
}

