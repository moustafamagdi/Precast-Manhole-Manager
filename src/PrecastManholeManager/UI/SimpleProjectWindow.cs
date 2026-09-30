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
        Close, Scan, ReviewOne, Make3D, DraftSheet, ExportExcel
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
            Button review = Button("2   Review Selected", 170, controls);
            Button view = Button("Review 3D", 125, controls);
            Button draft = Button("3   Draft Sheet", 160, controls);
            Button export = Button("Export Existing Excel", 177, controls);

            top.Children.Add(new TextBlock
            {
                Text = "Scan finds the manholes and isolates problem cases. " +
                    "Review Selected performs the detailed MEP/opening inspection " +
                    "without modifying the model. 3D creates a cropped Revit " +
                    "view. Draft Sheet creates a prototype Plan + 4 wall-facing " +
                    "orthographic views on one sheet for a selected READY manhole. " +
                    "Excel uses only previously SAVED fabrication data.",
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
            AddColumn("Foundation", "FoundationId", 108);
            AddColumn("Status", "State", 155);
            AddColumn("Type", "TypeName", 235);
            AddColumn("Reason / what needs review", "Problem", 395);
            AddColumn("3D view", "ViewName", 185);

            scan.Click += (s, e) => Choose(ProjectAction.Scan, false);
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
                    "Generate a DRAFT Plan + W1-W4 as orthographic views " +
                    "and attempt to place them on one sheet? This does " +
                    "NOT clean old cuts, create openings or issue " +
                    "shop drawings. Use a test RVT copy with an A1/A0 " +
                    "title block.",
                    "First Manhole Prototype",
                    MessageBoxButton.YesNo) != MessageBoxResult.Yes)
                    return;
                Choose(ProjectAction.DraftSheet, true);
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
