using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Hatco.PrecastManholeManager.Services;

namespace Hatco.PrecastManholeManager.UI
{
    internal enum ProjectAction
    {
        Close, Scan, NumberAll, ReviewOne, Make3D, DraftSheet, ProductionOne, SixRowLayoutSheet, ExportExcel
    }

    // Intentionally modal: the Revit command performs the selected operation
    // AFTER the dialog closes, then re-opens this same single window.
    // This avoids unsafe Revit API calls from live WPF UI event handlers.
    internal sealed class SimpleProjectWindow : Window
    {
        private readonly List<SimpleManholeItem> _all;
        private readonly DataGrid _grid;
        private readonly TextBox _filter;
        private readonly TextBlock _counts;
        private readonly CheckBox _onlyReview;
        public ProjectAction Action { get; private set; } = ProjectAction.Close;
        public SimpleManholeItem SelectedManhole { get; private set; }
        public double ViewMarginMm { get; private set; } = 350;
        public List<SimpleManholeItem> SheetCandidates { get; private set; } =
            new List<SimpleManholeItem>();

        public SimpleProjectWindow(List<SimpleManholeItem> items)
        {
            _all = items ?? new List<SimpleManholeItem>();
            Title = "HATCO | Precast Manhole Manager";
            Width = 1080;
            Height = 660;
            MinWidth = 750;
            MinHeight = 470;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.CanResize;
            var root = new DockPanel { Margin = new Thickness(16) };
            Content = root;

            var top = new StackPanel();
            DockPanel.SetDock(top, Dock.Top);
            root.Children.Add(top);
            top.Children.Add(new TextBlock
            {
                Text = "Precast Manhole Manager",
                FontSize = 22,
                FontWeight = FontWeights.SemiBold
            });
            top.Children.Add(new TextBlock
            {
                Text = "One-time project run:  1  SCAN    →    2  REVIEW    →    3  OUTPUT",
                Margin = new Thickness(0, 6, 0, 10)
            });
            _counts = new TextBlock
            {
                FontSize = 14,
                Margin = new Thickness(0, 0, 0, 10)
            };
            top.Children.Add(_counts);

            var controls = new WrapPanel
            {
                Margin = new Thickness(0, 0, 0, 10)
            };
            top.Children.Add(controls);
            Button scan = Button("1   Scan Project", 155, controls);
            Button number = Button("Assign Manhole Names", 175, controls);
            Button review = Button("2   Review Selected", 170, controls);
            Button view = Button("Review 3D", 125, controls);
            Button draft = Button("Create 2D Views", 150, controls);
            Button produce = Button("Generate Selected Manhole", 210, controls);
            Button six = Button("3   Test 6-Row Sheet", 175, controls);
            Button export = Button("Export Existing Excel", 177, controls);

            top.Children.Add(new TextBlock
            {
                Text = "Assign Manhole Names previews all IDs, preserves " +
                    "existing designations, and writes approved generated names " +
                    "to each foundation Mark. Scan finds manholes and " +
                    "isolates problem cases. " +
                    "Review Selected performs the detailed MEP/opening inspection " +
                    "without modifying the model. 3D creates a cropped Revit " +
                    "view. Create 2D Views makes a real Floor Plan and " +
                    "four Sections for one selected manhole. Generate Selected " +
                    "Manhole reviews actual MEP cuts and asks permission " +
                    "before creating real openings and its own sheet. " +
                    "The test six-row " +
                    "sheet attempts up to six clean, unplaced manholes at " +
                    "1:25 without touching your existing manual sheet. Excel reads " +
                    "only previously SAVED fabrication data.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            });

            var search = new WrapPanel
            {
                Margin = new Thickness(0, 0, 0, 8)
            };
            top.Children.Add(search);
            search.Children.Add(new TextBlock
            {
                Text = "Find:",
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
            AddColumn("Foundation", "FoundationId", 100);
            AddColumn("Manhole Name", "ManholeName", 152);
            AddColumn("Status", "State", 155);
            AddColumn("Type", "TypeName", 204);
            AddColumn("Reason / what needs review", "Problem", 330);
            AddColumn("3D view", "ViewName", 185);

            scan.Click += (s, e) => Choose(ProjectAction.Scan, false);
            number.Click += (s, e) => Choose(ProjectAction.NumberAll, false);
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
                if (row.State == "REVIEW")
                {
                    MessageBox.Show(this,
                        "This manhole is isolated for review. Choose " +
                        "a manhole with no recorded issues for the " +
                        "first draft prototype.");
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
            produce.Click += (sender, args) =>
            {
                SimpleManholeItem row = _grid.SelectedItem as SimpleManholeItem;
                if (row == null)
                {
                    MessageBox.Show(this, "Select ONE manhole first.");
                    return;
                }
                if (row.State == "REVIEW")
                {
                    MessageBox.Show(this, "Resolve this manhole's recorded " +
                        "review issues before production.");
                    return;
                }
                // Detailed scan runs AFTER the dialog closes. The user
                // receives actual sizes/counts plus a second approval
                // dialog before any physical Revit wall changes.
                Choose(ProjectAction.ProductionOne, true);
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
            UpdateCounts();
        }

        private static Button Button(string label, int width, Panel panel)
        {
            var button = new Button
            {
                Content = label,
                Width = width,
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
                "    |    Remaining: " + (_all.Count - review) +
                "    |    Nothing is deleted automatically";
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
            Action = requested;
            DialogResult = true;
        }
    }
}
