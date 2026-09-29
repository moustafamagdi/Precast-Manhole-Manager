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
using WpfTextBox = System.Windows.Controls.TextBox;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Hatco.PrecastManholeManager.Infrastructure;
using Hatco.PrecastManholeManager.Models;
using Hatco.PrecastManholeManager.Services;

namespace Hatco.PrecastManholeManager.UI
{
    internal sealed class OpeningPreviewWindow : Window
    {
        private static OpeningPreviewWindow _activeWindow;

        private readonly Document _document;
        private readonly List<int> _wallIds;
        private readonly List<PenetrationRecord> _records;
        private readonly ManholeDataRecord _manholeData;
        private readonly WpfTextBox _manholeNumberBox;
        private readonly WpfTextBox _clearanceBox;
        private readonly DataGrid _grid;
        private readonly TextBlock _status;
        private readonly OpeningSyncExternalEventHandler _syncHandler;
        private readonly ExternalEvent _syncEvent;
        private readonly Button _syncButton;
        private readonly Button _saveManholeButton;
        private readonly ManholeDataSyncExternalEventHandler _manholeHandler;
        private readonly ExternalEvent _manholeEvent;
        private bool _syncPending;
        private bool _manholePending;

        public static void ShowModeless(
            Document document,
            int foundationId,
            string wallSummary,
            IList<int> wallIds,
            IList<PenetrationRecord> records,
            ManholeDataRecord manholeData)
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

            _activeWindow = new OpeningPreviewWindow(document, foundationId, wallSummary, wallIds, records, manholeData);
            _activeWindow.Closed += (s, e) => _activeWindow = null;
            _activeWindow.Show();
        }

        private OpeningPreviewWindow(
            Document document,
            int foundationId,
            string wallSummary,
            IList<int> wallIds,
            IList<PenetrationRecord> records,
            ManholeDataRecord manholeData)
        {
            _document = document;
            _wallIds = wallIds?.ToList() ?? new List<int>();
            _records = records?.ToList() ?? new List<PenetrationRecord>();
            _manholeData = manholeData ?? throw new ArgumentNullException(nameof(manholeData));

            _syncHandler = new OpeningSyncExternalEventHandler(OnSyncCompleted);
            _syncEvent = ExternalEvent.Create(_syncHandler);

            _manholeHandler = new ManholeDataSyncExternalEventHandler(OnManholeSyncCompleted);
            _manholeEvent = ExternalEvent.Create(_manholeHandler);

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
                Text = "PRECAST MANHOLE MANAGER - PHASE 2 / 3 / 4",
                FontSize = 18,
                FontWeight = FontWeights.SemiBold
            });

            header.Children.Add(new TextBlock
            {
                Text = $"Foundation: {foundationId}    |    {wallSummary}    |    Detected: {_records.Count}",
                Margin = new Thickness(0, 4, 0, 4)
            });

            header.Children.Add(new TextBlock
            {
                Text =
                    $"Clear W1-W4: {_manholeData.ClearW1W4Mm:0.#} mm    |    " +
                    $"Clear W2-W3: {_manholeData.ClearW2W3Mm:0.#} mm    |    " +
                    $"Wall Height: {_manholeData.WallHeightMm:0.#} mm    |    " +
                    $"Base Thickness: {_manholeData.BaseThicknessMm:0.#} mm",
                Margin = new Thickness(0, 0, 0, 8)
            });

            var manholeControls = new WrapPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 8)
            };
            header.Children.Add(manholeControls);

            manholeControls.Children.Add(new TextBlock
            {
                Text = "Manhole No.:",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            });

            _manholeNumberBox = new WpfTextBox
            {
                Width = 150,
                Text = _manholeData.ManholeNumber ?? string.Empty,
                Margin = new Thickness(0, 0, 8, 0)
            };
            manholeControls.Children.Add(_manholeNumberBox);

            _saveManholeButton = new Button
            {
                Content = "Save Manhole Data",
                Padding = new Thickness(12, 4, 12, 4),
                Margin = new Thickness(0, 0, 8, 0)
            };
            _saveManholeButton.Click += SaveManhole_Click;
            manholeControls.Children.Add(_saveManholeButton);

            var controls = new WrapPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(controls);

            controls.Children.Add(new TextBlock
            {
                Text = "Clearance per side (mm):",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            });

            _clearanceBox = new WpfTextBox
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
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 8, 0)
            };
            export.Click += Export_Click;
            controls.Children.Add(export);

            _syncButton = new Button
            {
                Content = "Create / Update Openings",
                Padding = new Thickness(12, 4, 12, 4),
                ToolTip = "Creates native Revit wall openings for accepted rows. Round penetrations currently use a rectangular envelope."
            };
            _syncButton.Click += Sync_Click;
            controls.Children.Add(_syncButton);

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

            Closed += (s, e) =>
            {
                try { _syncEvent?.Dispose(); } catch { }
                try { _manholeEvent?.Dispose(); } catch { }
            };

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
                Binding = new WpfBinding(nameof(PenetrationRecord.Accepted))
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
                Binding = new WpfBinding(nameof(PenetrationRecord.ClearanceMm))
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
                Binding = new WpfBinding(path),
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

        private void SaveManhole_Click(object sender, RoutedEventArgs e)
        {
            if (_manholePending)
                return;

            string number = (_manholeNumberBox.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(number))
            {
                MessageBox.Show(
                    this,
                    "Enter a manhole number before saving.",
                    "Precast Manhole Manager",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            ManholeDataRecord data = CloneManholeData(_manholeData);
            data.ManholeNumber = number;

            _manholeHandler.SetRequest(new ManholeDataSyncRequest
            {
                Document = _document,
                Data = data
            });

            _manholePending = true;
            _saveManholeButton.IsEnabled = false;
            _status.Text = "Saving manhole data carrier in Revit...";

            ExternalEventRequest status = _manholeEvent.Raise();
            if (status != ExternalEventRequest.Accepted)
            {
                _manholePending = false;
                _saveManholeButton.IsEnabled = true;
                _status.Text = "Could not queue manhole data sync. ExternalEvent status: " + status;
            }
        }

        private void OnManholeSyncCompleted(ManholeDataSyncResult result)
        {
            _manholePending = false;
            _saveManholeButton.IsEnabled = true;

            if (result.Success)
            {
                _manholeData.ManholeNumber = result.ManholeNumber;
                _status.Text =
                    $"Manhole '{result.ManholeNumber}' saved. Carrier={result.CarrierElementId} | " +
                    $"Linked openings={result.LinkedOpenings} | Log: {result.LogPath}";
            }
            else
            {
                _status.Text = "Manhole data save failed. Log: " + result.LogPath;
                MessageBox.Show(
                    this,
                    result.Message + "\n\nLog:\n" + result.LogPath,
                    "Manhole Data Save Failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        private static ManholeDataRecord CloneManholeData(ManholeDataRecord d)
        {
            return new ManholeDataRecord
            {
                ManholeNumber = d.ManholeNumber,
                FoundationId = d.FoundationId,
                FoundationUniqueId = d.FoundationUniqueId,
                Wall1Id = d.Wall1Id,
                Wall2Id = d.Wall2Id,
                Wall3Id = d.Wall3Id,
                Wall4Id = d.Wall4Id,
                CenterXmm = d.CenterXmm,
                CenterYmm = d.CenterYmm,
                BaseTopZmm = d.BaseTopZmm,
                BaseThicknessMm = d.BaseThicknessMm,
                ClearW1W4Mm = d.ClearW1W4Mm,
                ClearW2W3Mm = d.ClearW2W3Mm,
                OuterW1W4Mm = d.OuterW1W4Mm,
                OuterW2W3Mm = d.OuterW2W3Mm,
                WallHeightMm = d.WallHeightMm
            };
        }

        private void Sync_Click(object sender, RoutedEventArgs e)
        {
            if (_syncPending)
                return;

            _grid.CommitEdit(DataGridEditingUnit.Cell, true);
            _grid.CommitEdit(DataGridEditingUnit.Row, true);

            List<PenetrationRecord> accepted = _records
                .Where(r => r.Accepted)
                .Select(CloneRecord)
                .ToList();

            int unresolved = accepted.Count(r => r.CutWidthMm <= 0 || r.CutHeightMm <= 0);
            if (unresolved > 0)
            {
                MessageBox.Show(
                    this,
                    unresolved + " accepted penetration(s) have unresolved opening dimensions. Review their sizes before creation.",
                    "Precast Manhole Manager",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            int roundCount = accepted.Count(r => string.Equals(r.Shape, "Round", StringComparison.OrdinalIgnoreCase));

            _syncHandler.SetRequest(new OpeningSyncRequest
            {
                Document = _document,
                ManholeWallIds = _wallIds.ToList(),
                AcceptedRecords = accepted
            });

            _syncPending = true;
            _syncButton.IsEnabled = false;
            _status.Text = "Opening sync queued in Revit..." +
                           (roundCount > 0 ? " Round penetrations will use rectangular envelopes in this build." : string.Empty);

            ExternalEventRequest status = _syncEvent.Raise();
            if (status != ExternalEventRequest.Accepted)
            {
                _syncPending = false;
                _syncButton.IsEnabled = true;
                _status.Text = "Could not queue opening sync. ExternalEvent status: " + status;
            }
        }

        private void OnSyncCompleted(OpeningSyncResult result)
        {
            _syncPending = false;
            _syncButton.IsEnabled = true;

            _status.Text = result + " | Log: " + result.LogPath;

            if (result.Failed > 0)
            {
                MessageBox.Show(
                    this,
                    result + "\n\nReview log:\n" + result.LogPath,
                    "Opening Sync Completed With Errors",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        private static PenetrationRecord CloneRecord(PenetrationRecord r)
        {
            return new PenetrationRecord
            {
                LinkName = r.LinkName,
                LinkInstanceId = r.LinkInstanceId,
                LinkedElementId = r.LinkedElementId,
                LinkedUniqueId = r.LinkedUniqueId,
                Category = r.Category,
                FamilyType = r.FamilyType,
                SystemName = r.SystemName,
                Size = r.Size,
                WallNumber = r.WallNumber,
                HostWallId = r.HostWallId,
                Xmm = r.Xmm,
                Ymm = r.Ymm,
                Zmm = r.Zmm,
                InvertMm = r.InvertMm,
                InvertAboveBaseMm = r.InvertAboveBaseMm,
                OffsetFromWallStartMm = r.OffsetFromWallStartMm,
                Notes = r.Notes,
                Shape = r.Shape,
                DiameterMm = r.DiameterMm,
                WidthMm = r.WidthMm,
                HeightMm = r.HeightMm,
                ClearanceMm = r.ClearanceMm,
                Accepted = r.Accepted
            };
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
