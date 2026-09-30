using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using WpfBinding = System.Windows.Data.Binding;
using Hatco.PrecastManholeManager.Services;

namespace Hatco.PrecastManholeManager.UI
{
    // Display-only modeless window. No Revit API calls are issued by UI events.
    internal sealed class UnifiedOpeningReviewWindow : Window
    {
        private static UnifiedOpeningReviewWindow _active;
        private readonly UnifiedOpeningReviewResult _result;
        private readonly TextBlock _status;

        public static void ShowReview(UnifiedOpeningReviewResult result,
            int foundationId, double clearance, double maxGap, double angle)
        {
            if (_active != null)
            {
                try { _active.Close(); }
                catch { /* stale window */ }
            }
            _active = new UnifiedOpeningReviewWindow(
                result, foundationId, clearance, maxGap, angle);
            _active.Closed += (s, e) => _active = null;
            _active.Show();
        }

        private UnifiedOpeningReviewWindow(UnifiedOpeningReviewResult result,
            int foundationId, double clearance, double maxGap, double angle)
        {
            _result = result;
            Title = "Manhole Unified Opening Review - READ ONLY";
            Width = 1500;
            Height = 760;
            MinWidth = 1000;
            MinHeight = 460;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.CanResize;

            try
            {
                new WindowInteropHelper(this).Owner =
                    Process.GetCurrentProcess().MainWindowHandle;
            }
            catch { }

            var root = new DockPanel { Margin = new Thickness(12) };
            Content = root;

            var top = new StackPanel { Orientation = Orientation.Vertical };
            DockPanel.SetDock(top, Dock.Top);
            root.Children.Add(top);

            top.Children.Add(new TextBlock
            {
                Text = "UNIFIED OPENING REVIEW (NO MODEL CHANGES)",
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 7)
            });
            top.Children.Add(new TextBlock
            {
                Text = "Foundation " + foundationId +
                    "   |   Actual crossings: " + result.ActualCount +
                    "   |   Virtual candidates: " + result.VirtualCount +
                    "   |   Duplicates skipped: " + result.DuplicatesSkipped,
                Margin = new Thickness(0, 0, 0, 5)
            });
            top.Children.Add(new TextBlock
            {
                Text = "Clearance/side: " + clearance.ToString("0.#") +
                    " mm   |   Virtual face gap <= " + maxGap.ToString("0.#") +
                    " mm   |   Plan approach <= " + angle.ToString("0.#") + "°",
                Margin = new Thickness(0, 0, 0, 5)
            });

            var audit = result.Audit;
            top.Children.Add(new TextBlock
            {
                Text = "Wall audit: Edited profiles " + audit.ProfileEditedWalls +
                    ", unknown profiles " + audit.ProfileUnknownWalls +
                    ", native manual openings " + audit.NativeUnmanaged +
                    ", recorded void cuts " + audit.VoidCutRelations +
                    ", unknown void status " + audit.VoidUnknownWalls,
                Margin = new Thickness(0, 0, 0, 5)
            });

            top.Children.Add(new TextBlock
            {
                Text = "IMPORTANT: edited profiles or embedded in-place cuts may contain existing openings " +
                    "that cannot yet be measured reliably. REVIEW does not authorize deletion or a new cut.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            });

            var actions = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            top.Children.Add(actions);
            var export = new Button
            {
                Content = "Export Unified Review CSV",
                Padding = new Thickness(12, 5, 12, 5),
                Margin = new Thickness(0, 0, 8, 0)
            };
            actions.Children.Add(export);
            export.Click += (s, e) =>
            {
                try
                {
                    string path = UnifiedOpeningReviewService.ExportCsv(_result);
                    _status.Text = "CSV exported: " + path;
                }
                catch (Exception ex)
                {
                    _status.Text = "CSV export failed: " + ex.Message;
                }
            };
            actions.Children.Add(new TextBlock
            {
                Text = "To change clearance/gap, rerun the command. This window cannot edit Revit.",
                VerticalAlignment = VerticalAlignment.Center
            });

            _status = new TextBlock
            {
                Text = "Review flags are preliminary. No profiles, void cuts or openings were altered.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0)
            };
            DockPanel.SetDock(_status, Dock.Bottom);
            root.Children.Add(_status);

            var grid = new DataGrid
            {
                AutoGenerateColumns = false,
                IsReadOnly = true,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                SelectionMode = DataGridSelectionMode.Extended,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                ItemsSource = result.Rows
            };
            root.Children.Add(grid);

            Add(grid, "Detection", "Detection", 110);
            Add(grid, "Wall", "Wall", 55);
            Add(grid, "Wall ID", "WallId", 90);
            Add(grid, "Source ID", "SourceId", 95);
            Add(grid, "MEP Size", "SourceSize", 105);
            Add(grid, "Gap (mm)", "GapMm", 90);
            Add(grid, "Slope %", "SlopePercent", 78);
            Add(grid, "Opening W×H", "OpeningSize", 130);
            Add(grid, "Opening bottom from base", "VerticalReference", 165);
            Add(grid, "Native", "Existing", 120);
            Add(grid, "Wall profile", "WallProfile", 130);
            Add(grid, "Void cuts", "VoidCuts", 85);
            Add(grid, "Status", "Status", 160);
            Add(grid, "Reasons", "Notes", 540);
            Add(grid, "Link", "LinkName", 230);
            Add(grid, "Service", "Service", 180);
        }

        private static void Add(DataGrid grid, string label,
            string property, double width)
        {
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = label,
                Binding = new WpfBinding(property)
                {
                    Mode = BindingMode.OneWay
                },
                Width = new DataGridLength(width)
            });
        }
    }

    internal sealed class UnifiedReviewSettingsWindow : Window
    {
        private readonly TextBox _clearance;
        private readonly TextBox _gap;
        private readonly TextBox _angle;

        public double ClearanceMm { get; private set; }
        public double MaxGapMm { get; private set; }
        public double ApproachAngleDeg { get; private set; }

        public UnifiedReviewSettingsWindow()
        {
            Title = "Unified Opening Review - Scan Settings";
            Width = 450;
            Height = 340;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            var root = new StackPanel { Margin = new Thickness(18) };
            Content = root;
            root.Children.Add(new TextBlock
            {
                Text = "READ-ONLY REVIEW. No opening reset or creation.",
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 15)
            });

            _clearance = AddInput(root, "Opening clearance per side (mm)", "50");
            _gap = AddInput(root, "Maximum virtual gap from wall face (mm)", "150");
            _angle = AddInput(root, "Maximum horizontal approach angle (degrees)", "15");

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            root.Children.Add(buttons);
            var run = new Button
            {
                Content = "Scan & Review",
                MinWidth = 130,
                Margin = new Thickness(0, 10, 10, 0)
            };
            var cancel = new Button
            {
                Content = "Cancel",
                MinWidth = 80,
                Margin = new Thickness(0, 10, 0, 0)
            };
            buttons.Children.Add(run);
            buttons.Children.Add(cancel);
            cancel.Click += (s, e) => DialogResult = false;
            run.Click += (s, e) =>
            {
                double clearance, gap, angle;
                if (!Parse(_clearance.Text, out clearance) ||
                    !Parse(_gap.Text, out gap) ||
                    !Parse(_angle.Text, out angle) ||
                    clearance < 0 || clearance > 500 ||
                    gap < 0 || gap > 1000 ||
                    angle < 0 || angle > 75)
                {
                    MessageBox.Show(this,
                        "Enter clearance 0-500 mm, gap 0-1000 mm, and angle 0-75°.");
                    return;
                }
                ClearanceMm = clearance;
                MaxGapMm = gap;
                ApproachAngleDeg = angle;
                DialogResult = true;
            };
        }

        private static TextBox AddInput(StackPanel root, string label,
            string defaultValue)
        {
            root.Children.Add(new TextBlock
            {
                Text = label,
                Margin = new Thickness(0, 0, 0, 3)
            });
            var box = new TextBox
            {
                Text = defaultValue,
                Width = 110,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 10)
            };
            root.Children.Add(box);
            return box;
        }

        private static bool Parse(string value, out double result)
        {
            if (double.TryParse(value, NumberStyles.Float,
                CultureInfo.CurrentCulture, out result) ||
                double.TryParse(value, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out result))
                return !double.IsNaN(result) && !double.IsInfinity(result);
            return false;
        }
    }
}
