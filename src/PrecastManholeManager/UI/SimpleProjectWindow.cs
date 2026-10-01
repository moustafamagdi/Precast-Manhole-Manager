using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Hatco.PrecastManholeManager.Services;

namespace Hatco.PrecastManholeManager.UI
{
    internal enum ProjectAction
    {
        Close, Scan, CleanScan, RecheckReviewOnly, NumberAll, ReviewOne, RecheckOne, Make3D, DraftSheet, ProductionOne, ProductionAll, DimensionOne, SixRowLayoutSheet, ExportExcel,
        ExistingOne, ExistingAll, ExistingDimensions, ExistingPicked, ExistingActiveView, RepairPicked, RepairActiveView, Review3DAll, SheetOnly, SheetsAll, SheetsAndDimensions, MissingOpeningsAll, CleanViewPresentation
    }

    // Intentionally modal: the Revit command performs the selected operation
    // AFTER the dialog closes, then re-opens this same single window.
    // This avoids unsafe Revit API calls from live WPF UI event handlers.
    internal sealed class SimpleProjectWindow : Window
    {
        private readonly List<SimpleManholeItem> _all;
        private readonly DataGrid _grid;
        private readonly TextBox _filter;
        private readonly TextBox _clearance;
        public double ClearanceMm { get; private set; }
        private readonly CheckBox _mergeOpenings = new CheckBox { Content = "Merge overlapping openings (one rectangular cut; includes gaps under 5 mm)", Margin = new Thickness(0, 3, 0, 5) };
        private readonly CheckBox _resetProfiles = new CheckBox { Content = "Reset edited wall profiles, one wall at a time (removes ALL profile edits)", Margin = new Thickness(0, 3, 0, 5) };
        public bool ResetWallProfiles => _resetProfiles.IsChecked == true;
        public bool MergeOverlappingOpenings => _mergeOpenings.IsChecked == true;
        private readonly CheckBox _timing = new CheckBox { Content = "Timing diagnostic: 3 new manholes only (slower)", Margin = new Thickness(0, 4, 0, 8) };
        public bool TimingDiagnostic => _timing.IsChecked == true;
        private readonly CheckBox _cropOrder = new CheckBox { Content = "Diagnostic experiment: set plan crop before activating", Margin = new Thickness(0, 0, 0, 8), IsEnabled = false };
        public bool CropOrderExperiment => TimingDiagnostic && _cropOrder.IsChecked == true;
        private readonly CheckBox _scopeOnly = new CheckBox { Content = "Diagnostic: timing only, without extra regeneration", Margin = new Thickness(0, 0, 0, 8), IsEnabled = false };
        public bool DiagnosticExtraRegeneration => _scopeOnly.IsChecked != true;
        private readonly TextBlock _counts;
        private readonly CheckBox _onlyReview;
        public ProjectAction Action { get; private set; } = ProjectAction.Close;
        public SimpleManholeItem SelectedManhole { get; private set; }
        public double ViewMarginMm { get; private set; } = 350;
        public List<SimpleManholeItem> SheetCandidates { get; private set; } =
            new List<SimpleManholeItem>();

        public SimpleProjectWindow(List<SimpleManholeItem> items, double clearanceMm = 50)
        {
            ClearanceMm = clearanceMm;
            _all = items ?? new List<SimpleManholeItem>();
            Title = "HATCO | Manhole Manager";
            Width = 1080;
            Height = 740;
            MinWidth = 860;
            MinHeight = 600;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.CanResize;
            var root = new DockPanel { Margin = new Thickness(16), Background = System.Windows.Media.Brushes.White };
            Content = root;

            var top = new StackPanel();
            DockPanel.SetDock(top, Dock.Top);
            root.Children.Add(top);
            top.Children.Add(new TextBlock
            {
                Text = "Manhole Manager",
                FontSize = 22,
                FontWeight = FontWeights.SemiBold
            });
            top.Children.Add(new TextBlock
            {
                Text = "Choose a task, select its scope, then run.",
                Margin = new Thickness(0, 6, 0, 10)
            });
            _counts = new TextBlock
            {
                FontSize = 14,
                Margin = new Thickness(0, 0, 0, 10)
            };
            top.Children.Add(_counts);
            var diagnostics = new StackPanel();
            diagnostics.Children.Add(_timing);
            diagnostics.Children.Add(_cropOrder);
            diagnostics.Children.Add(_scopeOnly);

            _timing.Checked += (s, e) => _scopeOnly.IsEnabled = true;
            _timing.Unchecked += (s, e) => { _scopeOnly.IsChecked = false; _scopeOnly.IsEnabled = false; };
            _timing.Checked += (s, e) => _cropOrder.IsEnabled = true;
            _timing.Unchecked += (s, e) => { _cropOrder.IsChecked = false; _cropOrder.IsEnabled = false; };

            var tabs = new TabControl { Height = 185, Margin = new Thickness(0, 0, 0, 12) };
            top.Children.Add(tabs);
            var openings = TaskPanel(tabs, "Openings", "Cut/update openings with or without sheets. Duct openings at wall ends shift inward at full size; required site movement is reported. Missing views defer dimensions.");
            var openingScope = Scope(openings, "Selected row", "Pick bases in Revit", "Current model view", "All detected manholes");
            openingScope.SelectedIndex = 1;
            openings.Children.Add(_mergeOpenings);
            openings.Children.Add(_resetProfiles);
            var missing = Button("Retry Missing Openings - All", 235, openings);
            missing.ToolTip = "Scan all manholes and create only wholly missing cuts. Existing cuts and edited profiles are preserved. Merge setting applies to new cuts.";
            missing.Click += (s, e) => Choose(ProjectAction.MissingOpeningsAll, false);
            var runOpenings = Button("Run Openings", 190, openings);
            runOpenings.Click += (s, e) => {
                var actions = new[] { ProjectAction.ExistingOne, ProjectAction.ExistingPicked, ProjectAction.ExistingActiveView, ProjectAction.ExistingAll };
                Choose(actions[openingScope.SelectedIndex], openingScope.SelectedIndex == 0);
            };

            var dimensionPanel = TaskPanel(tabs, "Dimensions", "Rebuild tool-owned opening, body and base dimensions. Manual dimensions are preserved; moved tool dimensions are reset.");
            var dimensionScope = Scope(dimensionPanel, "Selected row", "All prepared manholes");
            Button dimensions = Button("Update Dimensions", 190, dimensionPanel);

            var repairs = TaskPanel(tabs, "Base Repair", "Repair openings below the wall: lower the base, leave 100 mm below the opening, keep wall tops and restore pins. Sheets are not required.");
            var repairScope = Scope(repairs, "Pick bases in Revit", "Current model view");
            var runRepair = Button("Repair Low Bases", 190, repairs);
            runRepair.Click += (s, e) => Choose(repairScope.SelectedIndex == 0 ? ProjectAction.RepairPicked : ProjectAction.RepairActiveView, false);

            var sheets = TaskPanel(tabs, "Views & Sheets", "Create missing documentation or run the full workflow. Generate / Update All can create views and sheets and arrange prepared rows.");
            var sheetButtons = new WrapPanel(); sheets.Children.Add(sheetButtons);
            Button draft = Button("Views - Selected", 180, sheetButtons);
            Button produce = Button("Prepare Sheet - Selected", 190, sheetButtons);
            Button sheetsAll = Button("Prepare Sheets - All", 190, sheetButtons);
            sheetsAll.Click += (s, e) => Choose(ProjectAction.SheetsAll, false);
            Button overnight = Button("Sheets + Dimensions - All", 215, sheetButtons);
            overnight.ToolTip = "Unattended: prepare/reuse sheets first, save, then dimension all prepared manholes. Review status does not exclude them. Saves the current RVT; does not run openings.";
            overnight.Click += (s, e) => Choose(ProjectAction.SheetsAndDimensions, false);
            Button batch = Button("Generate / Update All", 190, sheetButtons);
            batch.Click += (s, e) => Choose(ProjectAction.ProductionAll, false);

            var presentation = Button("Plan Marks + Viewport Type - All", 265, sheetButtons);
            presentation.ToolTip = "Show only each manhole's W1-W4 markers and use NO BUBBLE NTS for its viewports. Keeps sheet positions and scales.";
            presentation.Click += (s, e) => Choose(ProjectAction.CleanViewPresentation, false);

            var reviewPanel = TaskPanel(tabs, "Scan & Review", "Scan the project, recheck recorded issues, or inspect the manhole selected in the table below.");
            var reviewButtons = new WrapPanel(); reviewPanel.Children.Add(reviewButtons);
            Button scan = Button("Scan Project", 155, reviewButtons);
            Button cleanScan = Button("Clean Scan - All", 170, reviewButtons);
            Button recheckReview = Button("Recheck Review Only", 180, reviewButtons);
            recheckReview.Click += (s, e) => Choose(ProjectAction.RecheckReviewOnly, false);
            recheckReview.ToolTip = "Recheck all OPEN review cases in this model, regardless of the table filter. Does not change model geometry.";
            Button recheck = Button("Recheck Selected", 170, reviewButtons);
            Button review = Button("Inspect Selected", 170, reviewButtons);
            Button view = Button("Show Review 3D", 160, reviewButtons);
            var review3Ds = Button("3D - All Review Cases", 190, reviewButtons);
            review3Ds.Click += (s, e) => Choose(ProjectAction.Review3DAll, false);
            review3Ds.ToolTip = "Create/update 3D views for OPEN issues in this model's review register. Run Clean Scan to refresh geometry issues first.";

            var advanced = TaskPanel(tabs, "Setup & Advanced", "Project numbering, exports and layout experiments. Performance settings apply only to Generate / Update All.");
            var advancedButtons = new WrapPanel(); advanced.Children.Add(advancedButtons);
            Button number = Button("Assign Internal MH IDs", 190, advancedButtons);
            Button export = Button("Export Existing Excel", 190, advancedButtons);
            Button six = Button("Test 6-Row Sheet", 180, advancedButtons);
            advanced.Children.Add(diagnostics);
            var openingSettings = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
            top.Children.Add(openingSettings);
            openingSettings.Children.Add(new TextBlock {
                Text = "Clearance per side (mm):", VerticalAlignment = VerticalAlignment.Center });
            _clearance = new TextBox { Width = 85, Margin = new Thickness(8, 0, 12, 0),
                Text = clearanceMm.ToString("0.###", CultureInfo.CurrentCulture) };
            openingSettings.Children.Add(_clearance);
            openingSettings.Children.Add(new TextBlock {
                Text = "Generate also updates existing tool openings and their sheet.",
                VerticalAlignment = VerticalAlignment.Center });

            var search = new WrapPanel
            {
                Margin = new Thickness(0, 0, 0, 8)
            };
            top.Children.Add(search);
            search.Children.Add(new TextBlock
            {
                Text = "Find MH / ID:",
                VerticalAlignment = VerticalAlignment.Center
            });
            _filter = new TextBox
            {
                Width = 240,
                Margin = new Thickness(8, 0, 18, 0)
            };
            search.Children.Add(_filter);
            _onlyReview = new CheckBox
            {
                Content = "Only show review cases",
                VerticalAlignment = VerticalAlignment.Center
            };
            search.Children.Add(_onlyReview);

            var bottom = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            DockPanel.SetDock(bottom, Dock.Bottom);
            root.Children.Add(bottom);
            var close = Button("Close", 105, bottom);
            close.Click += (s, e) =>
            {
                Action = ProjectAction.Close;
                DialogResult = false;
            };

            _grid = new DataGrid
            {
                AutoGenerateColumns = false,
                ItemsSource = _all,
                IsReadOnly = true,
                CanUserAddRows = false,
                SelectionMode = DataGridSelectionMode.Single,
                HeadersVisibility = DataGridHeadersVisibility.Column
            };
            root.Children.Add(_grid);
            AddColumn("Internal MH ID", "ManholeName", 152);
            AddColumn("Foundation", "FoundationId", 100);
            AddColumn("Status", "State", 155);
            AddColumn("Reason / what needs review", "Problem", 330);
            AddColumn("Type", "TypeName", 204);
            AddColumn("3D view", "ViewName", 185);

            cleanScan.Click += (s, e) => Choose(ProjectAction.CleanScan, false);
            scan.Click += (s, e) => Choose(ProjectAction.Scan, false);
            number.Click += (s, e) => Choose(ProjectAction.NumberAll, false);
            recheck.Click += (s, e) => Choose(ProjectAction.RecheckOne, true);
            review.Click += (s, e) => Choose(ProjectAction.ReviewOne, true);
            view.Click += (s, e) => Choose(ProjectAction.Make3D, true);
            draft.Click += (s, e) =>
            {
                SimpleManholeItem row = _grid.SelectedItem as SimpleManholeItem;
                if (row == null)
                {
                    MessageBox.Show(this, "Select one manhole first.");
                    return;
                }
                if (MessageBox.Show(this,
                    "Create a real Revit FLOOR PLAN plus four " +
                    "SECTIONS (W1-W4) for this ONE manhole? " +
                    "No sheet, viewports or titleblock will be created. " +
                    "You can set scales and arrange the views yourself. " +
                    "No wall/opening geometry will change.",
                    "First Manhole Prototype",
                    MessageBoxButton.YesNo) != MessageBoxResult.Yes)
                    return;
                Choose(ProjectAction.DraftSheet, true);
            };
            produce.Click += (s, e) => Choose(ProjectAction.SheetOnly, true);
            dimensions.Click += (sender, args) => Choose(dimensionScope.SelectedIndex == 0 ? ProjectAction.DimensionOne : ProjectAction.ExistingDimensions, dimensionScope.SelectedIndex == 0);
            six.Click += (sender, args) =>
            {
                // Test ONE sheet only. The view-generation engine validates
                // each foundation again and skips isolated/problematic cases.
                SheetCandidates = _all.Where(x => x.State != "REVIEW")
                    .OrderBy(x => x.FoundationId).ToList();
                if (SheetCandidates.Count == 0)
                {
                    MessageBox.Show(this,
                        "No manholes without recorded issues. Scan the project first.");
                    return;
                }
                if (MessageBox.Show(this,
                    "Create ONE TEST SHEET with up to six eligible manholes " +
                    "at 1:25? Each row: PLAN, W1, W2, W3, W4. " +
                    "A new sheet will be created; your manually arranged " +
                    "sheet will NOT be edited. For the same titleblock, " +
                    "open your sample sheet BEFORE launching this tool. " +
                    "Large/placed/problematic rows will be skipped.",
                    "Test six-row sheet", MessageBoxButton.YesNo) !=
                    MessageBoxResult.Yes) return;
                Choose(ProjectAction.SixRowLayoutSheet, false);
            };
            export.Click += (s, e) => Choose(ProjectAction.ExportExcel, false);
            _grid.MouseDoubleClick += (s, e) =>
            {
                if (_grid.SelectedItem is SimpleManholeItem)
                    Choose(ProjectAction.ReviewOne, true);
            };
            _filter.TextChanged += (s, e) => Filter();
            _onlyReview.Checked += (s, e) => Filter();
            _onlyReview.Unchecked += (s, e) => Filter();
            UpdateCounts();
        }

        private static StackPanel TaskPanel(TabControl tabs, string title, string description)
        {
            var panel = new StackPanel { Margin = new Thickness(14) };
            panel.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12) });
            tabs.Items.Add(new TabItem { Header = title, Padding = new Thickness(12, 7, 12, 7),
                Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } });
            return panel;
        }

        private static ComboBox Scope(Panel parent, params string[] choices)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
            row.Children.Add(new TextBlock { Text = "Apply to:", VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0) });
            var scope = new ComboBox { ItemsSource = choices, SelectedIndex = 0, Width = 260, Padding = new Thickness(6) };
            row.Children.Add(scope); parent.Children.Add(row);
            return scope;
        }

        private static Button Button(string label, int width, Panel panel)
        {
            var button = new Button
            {
                Content = label,
                Width = width,
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(6, 7, 6, 7),
                Margin = new Thickness(0, 0, 10, 6)
            };
            panel.Children.Add(button);
            return button;
        }

        private void AddColumn(string label, string property, double width)
        {
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = label,
                Binding = new Binding(property),
                Width = new DataGridLength(width)
            });
        }

        private void Filter()
        {
            string q = (_filter.Text ?? "").Trim();
            IEnumerable<SimpleManholeItem> rows = _all;
            if (_onlyReview.IsChecked == true)
                rows = rows.Where(x => x.State == "REVIEW");
            if (q.Length > 0)
                rows = rows.Where(x =>
                    x.FoundationId.ToString().Contains(q) ||
                    (x.ManholeName ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (x.Problem ?? "").IndexOf(q,
                        StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (x.TypeName ?? "").IndexOf(q,
                        StringComparison.OrdinalIgnoreCase) >= 0);
            _grid.ItemsSource = rows.ToList();
        }

        private void UpdateCounts()
        {
            int review = _all.Count(x => x.State == "REVIEW");
            _counts.Text = "Detected: " + _all.Count +
                "    |    For review: " + review +
                "    |    No recorded issue: " + (_all.Count - review) +
                "";
        }

        private void Choose(ProjectAction requested, bool requireRow)
        {
            SelectedManhole = _grid.SelectedItem as SimpleManholeItem;
            if (requireRow && SelectedManhole == null)
            {
                MessageBox.Show(this,
                    "Choose one manhole row first.");
                return;
            }
            if (requested == ProjectAction.ExportExcel)
            {
                if (MessageBox.Show(this,
                    "Export workbook for manholes whose data has ALREADY " +
                    "been saved by the existing workflow? This does not " +
                    "generate any Revit sheets.",
                    "Manufacturer Excel", MessageBoxButton.YesNo) !=
                    MessageBoxResult.Yes) return;
            }
            double clearance;
            bool valid = (double.TryParse(_clearance.Text, NumberStyles.Float,
                CultureInfo.CurrentCulture, out clearance) ||
                double.TryParse(_clearance.Text, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out clearance)) &&
                !double.IsNaN(clearance) && !double.IsInfinity(clearance) && clearance >= 0;
            if (!valid && (requested == ProjectAction.ProductionOne || requested == ProjectAction.ProductionAll ||
                requested == ProjectAction.MissingOpeningsAll || requested == ProjectAction.ExistingOne || requested == ProjectAction.ExistingAll ||
                requested == ProjectAction.ExistingPicked || requested == ProjectAction.ExistingActiveView ||
                requested == ProjectAction.RepairPicked || requested == ProjectAction.RepairActiveView ||
                requested == ProjectAction.ReviewOne || requested == ProjectAction.RecheckOne || requested == ProjectAction.CleanScan || requested == ProjectAction.RecheckReviewOnly))
            {
                MessageBox.Show(this, "Enter a finite clearance of zero or more millimeters per side.");
                _clearance.Focus();
                return;
            }
            if (valid) ClearanceMm = clearance;
            Action = requested;
            DialogResult = true;
        }
    }
}
