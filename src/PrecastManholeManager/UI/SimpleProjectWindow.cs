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
        ExistingOne, ExistingAll, ExistingDimensions, ExistingPicked, ExistingActiveView, RepairPicked, RepairActiveView, Review3DAll, SheetOnly, SheetsAll, SheetsAndDimensions, CleanViewPresentation
    }

    // Intentionally modal: the Revit command performs the selected operation
    // AFTER the dialog closes, then re-opens this same single window.
    // This avoids unsafe Revit API calls from live WPF UI event handlers.
    internal sealed class SimpleProjectWindow : Window
    {
        private readonly List<SimpleManholeItem> _all;
        private static int _lastTab;
        private static string _lastSearch = "";
        private static bool _lastReviewFilter;
        private static int? _lastFoundation;
        public bool MissingOpeningsOnly { get; private set; }
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
                Text = "1. Check & Review  >  2. Openings & Repair  >  3. Drawings",
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

            var tabs = new TabControl { Height = 270, Margin = new Thickness(0, 0, 0, 12) };
            top.Children.Add(tabs);

            var reviewPanel = TaskPanel(tabs, "1. Check & Review", "Recheck current geometry and missing openings. Checking does not create openings or drawings.");
            var checkScope = Scope(reviewPanel, "All manholes (Clean Scan)", "Review cases only", "Selected row");
            var reviewButtons = new WrapPanel(); reviewPanel.Children.Add(reviewButtons);
            var check = Button("Run Check", 155, reviewButtons);
            check.Click += (sender, args) => Choose(new[] { ProjectAction.CleanScan, ProjectAction.RecheckReviewOnly, ProjectAction.RecheckOne }[checkScope.SelectedIndex], checkScope.SelectedIndex == 2);
            var inspect = Button("Inspect Selected", 160, reviewButtons);
            inspect.Click += (sender, args) => Choose(ProjectAction.ReviewOne, true);
            var reviewViewScope = Choice(reviewPanel, "3D views:", "Selected row", "All review cases");
            var reviewView = Button("Create / Open Review 3D", 225, reviewPanel);
            reviewView.Click += (sender, args) => Choose(reviewViewScope.SelectedIndex == 0 ? ProjectAction.Make3D : ProjectAction.Review3DAll, reviewViewScope.SelectedIndex == 0);

            var openings = TaskPanel(tabs, "2. Openings & Repair", "Pipes and ducts only. Sheets are not required. Failed cuts remain in review.");
            var openingScope = Scope(openings, "Selected row", "Pick bases in Revit", "Current model view", "All detected manholes");
            openingScope.SelectedIndex = 1;
            var openingMode = Choice(openings, "Operation:", "Create / update openings", "Retry missing openings only");
            var openingHint = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 5) };
            openings.Children.Add(openingHint);
            var openingActions = new WrapPanel(); openings.Children.Add(openingActions);
            var runOpenings = Button("Run Openings", 180, openingActions);
            openingActions.Children.Add(_mergeOpenings);
            var resetPanel = new StackPanel(); resetPanel.Children.Add(_resetProfiles);
            openings.Children.Add(new Expander { Header = "Wall profile repair options", Content = resetPanel });
            Action updateOpeningMode = () => {
                MissingOpeningsOnly = openingMode.SelectedIndex == 1;
                _resetProfiles.IsEnabled = !MissingOpeningsOnly;
                if (MissingOpeningsOnly) _resetProfiles.IsChecked = false;
                openingHint.Text = MissingOpeningsOnly
                    ? "Creates wholly missing cuts in the chosen scope. Existing cuts and wall profiles are preserved."
                    : "Creates cuts and updates existing tool openings using the clearance below.";
            };
            openingMode.SelectionChanged += (sender, args) => updateOpeningMode();
            updateOpeningMode();
            runOpenings.Click += (sender, args) => Choose(new[] { ProjectAction.ExistingOne, ProjectAction.ExistingPicked, ProjectAction.ExistingActiveView, ProjectAction.ExistingAll }[openingScope.SelectedIndex], openingScope.SelectedIndex == 0);
            var repairs = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            repairs.Children.Add(new TextBlock { Text = "Lower eligible bases to leave 100 mm below openings; extend walls and restore pins.", TextWrapping = TextWrapping.Wrap });
            var repairScope = Scope(repairs, "Pick bases in Revit", "Current model view");
            var runRepair = Button("Repair Low Bases", 190, repairs);
            runRepair.Click += (sender, args) => Choose(repairScope.SelectedIndex == 0 ? ProjectAction.RepairPicked : ProjectAction.RepairActiveView, false);
            openings.Children.Add(new Expander { Header = "Base repair", Content = repairs, Margin = new Thickness(0, 6, 0, 0) });

            var drawings = TaskPanel(tabs, "3. Drawings", "Review status does not block sheet preparation. Existing views are reused; moved manholes still need their view extents checked.");
            var drawingTask = Choice(drawings, "Task:", "Sheets + dimensions - All", "Prepare sheets - All", "Prepare sheet - Selected", "Dimensions - All prepared", "Dimensions - Selected", "Plan marks + viewport type - All");
            drawingTask.Width = 350;
            var drawingHint = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
            drawings.Children.Add(drawingHint);
            string[] drawingHints = {
                "Unattended: prepare/reuse sheets, save, then dimension prepared manholes. Saves the current RVT. Does not run openings.",
                "Prepare/reuse sheets and five views per manhole. No opening or dimension pass.",
                "Prepare/reuse the selected manhole's sheet and five views. No opening pass.",
                "Rebuild tool dimensions for all prepared manholes. Manual dimensions are preserved.",
                "Rebuild tool dimensions for the selected manhole. Manual dimensions are preserved.",
                "Keep only each plan's own W1-W4 markers and set manhole viewports to NO BUBBLE NTS. Keep sheet positions." };
            drawingHint.Text = drawingHints[0];
            drawingTask.SelectionChanged += (sender, args) => drawingHint.Text = drawingHints[drawingTask.SelectedIndex];
            var runDrawings = Button("Run Drawing Task", 195, drawings);
            runDrawings.Click += (sender, args) => Choose(new[] { ProjectAction.SheetsAndDimensions, ProjectAction.SheetsAll, ProjectAction.SheetOnly, ProjectAction.ExistingDimensions, ProjectAction.DimensionOne, ProjectAction.CleanViewPresentation }[drawingTask.SelectedIndex], drawingTask.SelectedIndex == 2 || drawingTask.SelectedIndex == 4);

            var advanced = TaskPanel(tabs, "Setup & Advanced", "Numbering, exports and optional diagnostic tools. Use Drawings for normal sheet preparation.");
            var advancedButtons = new WrapPanel(); advanced.Children.Add(advancedButtons);
            Button number = Button("Assign Internal MH IDs", 190, advancedButtons);
            Button export = Button("Export Existing Excel", 190, advancedButtons);
            var experiments = new StackPanel();
            var experimentButtons = new WrapPanel(); experiments.Children.Add(experimentButtons);
            Button scan = Button("Geometry Inventory", 175, experimentButtons);
            scan.ToolTip = "Legacy geometry inventory. Use Run Check for current service and missing-opening checks.";
            Button draft = Button("Views Only - Selected", 185, experimentButtons);
            Button six = Button("Test 6-Row Sheet", 175, experimentButtons);
            Button batch = Button("Combined Sheets + Openings", 240, experimentButtons);
            batch.ToolTip = "Legacy combined workflow; creates sheets and also changes openings. Diagnostics below apply only here.";
            batch.Click += (sender, args) => Choose(ProjectAction.ProductionAll, false);
            experiments.Children.Add(diagnostics);
            advanced.Children.Add(new Expander { Header = "Legacy workflows & performance diagnostics", Content = experiments });
            tabs.SelectedIndex = Math.Max(0, Math.Min(_lastTab, tabs.Items.Count - 1));
            tabs.SelectionChanged += (sender, args) => { if (args.Source == tabs) _lastTab = tabs.SelectedIndex; };
            var openingSettings = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
            top.Children.Add(openingSettings);
            openingSettings.Children.Add(new TextBlock {
                Text = "Clearance per side (mm):", VerticalAlignment = VerticalAlignment.Center });
            _clearance = new TextBox { Width = 85, Margin = new Thickness(8, 0, 12, 0),
                Text = clearanceMm.ToString("0.###", CultureInfo.CurrentCulture) };
            openingSettings.Children.Add(_clearance);
            openingSettings.Children.Add(new TextBlock {
                Text = "Used by checks, openings and base repair.",
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

            scan.Click += (s, e) => Choose(ProjectAction.Scan, false);
            number.Click += (s, e) => Choose(ProjectAction.NumberAll, false);
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
            _filter.Text = _lastSearch;
            _onlyReview.IsChecked = _lastReviewFilter;
            _grid.SelectedItem = _all.FirstOrDefault(x => x.FoundationId == _lastFoundation);
            Closed += (sender, args) => {
                _lastSearch = _filter.Text;
                _lastReviewFilter = _onlyReview.IsChecked == true;
                _lastFoundation = (_grid.SelectedItem as SimpleManholeItem)?.FoundationId;
            };
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
            return Choice(parent, "Apply to:", choices);
        }

        private static ComboBox Choice(Panel parent, string label, params string[] choices)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center,
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
                requested == ProjectAction.ExistingOne || requested == ProjectAction.ExistingAll ||
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
