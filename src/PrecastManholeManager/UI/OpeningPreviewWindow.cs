using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using Hatco.PrecastManholeManager.Infrastructure;
using Hatco.PrecastManholeManager.Models;

namespace Hatco.PrecastManholeManager.UI
{
    internal sealed class OpeningPreviewWindow : Window
    {
        private static OpeningPreviewWindow _activeWindow;

        private readonly List<PenetrationRecord> _records;
        private readonly TextBox _clearanceBox;
        private readonly DataGrid _grid;
        private readonly TextBlock _status;

        public static void ShowModeless(
            int foundationId,
            string wallSummary,
            IList<PenetrationRecord> records)
        {
            if (_activeWindow != null)
            {
                try
                {
                    _activeWindow.Activate();
                    return;
                }
                catch
                {
                    _activeWindow = null;
                }
            }

            _activeWindow = new OpeningPreviewWindow(foundationId, wallSummary, records);
            _activeWindow.Closed += (s, e) => _activeWindow = null;
            _activeWindow.Show();
        }

        private OpeningPreviewWindow(
            int foundationId,
            string wallSummary,
            IList<PenetrationRecord> records)
        {
            _records = records?.ToList() ?? new List<PenetrationRecord>();

            Title = "Precast Manhole Manager - Opening Preview";
            Width = 1320;
            Height = 720;
            MinWidth = 950;
            MinHeight = 500;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.CanResize;

            try
            {
                new WindowInteropHelper(this).Owner = Process.GetCurrentProcess().MainWindowHandle;
            }
            catch
            {
                // Owner assignment is optional. Window can still operate modelessly.
            }

            var root = new DockPanel { Margin = new Thickness(12) };
            Content = root;

            var header = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Margin = new Thickness(0, 0, 0, 10)
            };
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            header.Children.Add(new TextBlock
            {
                Text = "PHASE 2 - OPENING PREVIEW",
                FontSize = 18,
                FontWeight = FontWeights.SemiBold
            });

            header.Children.Add(new TextBlock
            {
                Text = $"Foundation: {foundationId}    |    {wallSummary}    |    Detected: {_records.Count}",
                Margin = new Thickness(0, 4, 0, 8)
            });

            var controls = new WrapPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(controls);

            controls.Children.Add(new TextBlock
            {
                Text = "Clearance per side (mm):",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            });

            _clearanceBox = new TextBox
            {
                Width = 80,
                Text = "50",
                Margin = new Thickness(0, 0, 8, 0)
            };
            controls.Children.Add(_clearanceBox);

            var apply = new Button
            {
                Content = "Apply Clearance",
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 8, 0)
            };
            apply.Click += ApplyClearance_Click;
            controls.Children.Add(apply);

            var selectAll = new Button
            {
                Content = "Select All",
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 8, 0)
            };
            selectAll.Click += (s, e) => SetAccepted(true);
            controls.Children.Add(selectAll);

            var clearAll = new Button
            {
                Content = "Clear All",
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 8, 0)
            };
            clearAll.Click += (s, e) => SetAccepted(false);
            controls.Children.Add(clearAll);

            var export = new Button
            {
                Content = "Export Accepted CSV",
                Padding = new Thickness(10, 4, 10, 4)
            };
            export.Click += Export_Click;
            controls.Children.Add(export);

            _grid = BuildGrid();
            _grid.ItemsSource = _records;
            root.Children.Add(_grid);

            var footer = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);

            _status = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center
            };
            footer.Children.Add(_status);

            var close = new Button
            {
                Content = "Close",
                Width = 90,
                Padding = new Thickness(8, 5, 8, 5),
                HorizontalAlignment = HorizontalAlignment.Right
            };
            close.Click += (s, e) => Close();
            DockPanel.SetDock(close, Dock.Right);
            footer.Children.Add(close);

            UpdateStatus();
        }

        private DataGrid BuildGrid()
        {
            var grid = new DataGrid
            {
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                IsReadOnly = false,
                SelectionMode = DataGridSelectionMode.Extended,
                SelectionUnit = DataGridSelectionUnit.FullRow,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal
            };

            ScrollViewer.SetHorizontalScrollBarVisibility(grid, ScrollBarVisibility.Auto);
            ScrollViewer.SetVerticalScrollBarVisibility(grid, ScrollBarVisibility.Auto);
            grid.CurrentCellChanged += (s, e) => UpdateStatus();

            grid.Columns.Add(new DataGridCheckBoxColumn
            {
                Header = "Use",
                Binding = new Binding(nameof(PenetrationRecord.Accepted))
                {
                    Mode = BindingMode.TwoWay,
                    UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
                },
                Width = 55
            });

            grid.Columns.Add(TextColumn("Wall", nameof(PenetrationRecord.WallLabel), 55));
            grid.Columns.Add(TextColumn("Category", nameof(PenetrationRecord.Category), 90));
            grid.Columns.Add(TextColumn("Service", nameof(PenetrationRecord.SystemName), 120));
            grid.Columns.Add(TextColumn("MEP Size", nameof(PenetrationRecord.Size), 95));
            grid.Columns.Add(TextColumn("Shape", nameof(PenetrationRecord.Shape), 85));
            grid.Columns.Add(TextColumn("Offset mm", nameof(PenetrationRecord.OffsetDisplay), 90));
            grid.Columns.Add(TextColumn("Invert from Base mm", nameof(PenetrationRecord.InvertAboveBaseDisplay), 125));
            grid.Columns.Add(TextColumn("Absolute Invert mm", nameof(PenetrationRecord.InvertDisplay), 120));

            grid.Columns.Add(new DataGridTextColumn
            {
                Header = "Clr/Side mm",
                Binding = new Binding(nameof(PenetrationRecord.ClearanceMm))
                {
                    Mode = BindingMode.TwoWay,
                    UpdateSourceTrigger = UpdateSourceTrigger.LostFocus,
                    StringFormat = "0.#"
                },
                Width = 90
            });

            grid.Columns.Add(TextColumn("Opening", nameof(PenetrationRecord.OpeningSize), 120));
            grid.Columns.Add(TextColumn("Link", nameof(PenetrationRecord.LinkName), 260));
            grid.Columns.Add(TextColumn("Element ID", nameof(PenetrationRecord.LinkedElementId), 95));
            grid.Columns.Add(TextColumn("Notes", nameof(PenetrationRecord.Notes), 260));

            return grid;
        }

        private static DataGridTextColumn TextColumn(string header, string path, double width)
        {
            return new DataGridTextColumn
            {
                Header = header,
                Binding = new Binding(path),
                Width = width,
                IsReadOnly = true
            };
        }

        private void ApplyClearance_Click(object sender, RoutedEventArgs e)
        {
            if (!double.TryParse(_clearanceBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double clearance) &&
                !double.TryParse(_clearanceBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out clearance))
            {
                MessageBox.Show(this, "Enter a valid clearance in millimeters.", "Precast Manhole Manager",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (clearance < 0 || clearance > 1000)
            {
                MessageBox.Show(this, "Clearance must be between 0 and 1000 mm.", "Precast Manhole Manager",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            foreach (PenetrationRecord record in _records)
                record.ClearanceMm = clearance;

            _grid.Items.Refresh();
            UpdateStatus();
        }

        private void SetAccepted(bool accepted)
        {
            foreach (PenetrationRecord record in _records)
                record.Accepted = accepted;

            _grid.Items.Refresh();
            UpdateStatus();
        }

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            _grid.CommitEdit(DataGridEditingUnit.Cell, true);
            _grid.CommitEdit(DataGridEditingUnit.Row, true);

            List<PenetrationRecord> accepted = _records.Where(r => r.Accepted).ToList();
            if (accepted.Count == 0)
            {
                MessageBox.Show(this, "No penetrations are selected for export.", "Precast Manhole Manager",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                string path = CsvExporter.Export(accepted);
                _status.Text = $"Exported {accepted.Count} accepted penetration(s): {path}";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "CSV Export Failed",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void UpdateStatus()
        {
            int accepted = _records.Count(r => r.Accepted);
            int review = _records.Count(r => string.Equals(r.OpeningSize, "Review", StringComparison.OrdinalIgnoreCase));
            _status.Text = $"Accepted: {accepted}/{_records.Count}    |    Need size review: {review}";
        }
    }
}
