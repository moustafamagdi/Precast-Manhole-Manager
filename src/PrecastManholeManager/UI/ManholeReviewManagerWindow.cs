using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Hatco.PrecastManholeManager.Services;

namespace Hatco.PrecastManholeManager.UI
{
    internal sealed class ReviewViewListItem
    {
        public bool Selected { get; set; } = false;
        public ManholeReviewIssue Issue { get; set; }
        public int FoundationId => Issue.FoundationId;
        public string Severity => Issue.Severity;
        public string Status => Issue.Status;
        public string Reason => Issue.Reason;
        public string View => Issue.ViewName ?? "";
        public string Walls => Issue.WallIds ?? "";
        public string LastDetected => Issue.UpdatedUtc ?? "";
    }

    internal sealed class ManholeReviewManagerWindow : Window
    {
        private readonly TextBox _margin;
        private readonly List<ReviewViewListItem> _items;
        public List<ManholeReviewIssue> SelectedIssues { get; private set; }
        public double MarginMm { get; private set; }
        public bool CreateViews { get; private set; }
        public bool StatusModified { get; private set; }

        public ManholeReviewManagerWindow(
            IList<ManholeReviewIssue> issues, string registerPath)
        {
            Title = "Precast Manhole Manager - Isolated Review Queue";
            Width = 1190;
            Height = 650;
            MinWidth = 870;
            MinHeight = 440;
            ResizeMode = ResizeMode.CanResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            _items = issues.OrderByDescending(x => x.UpdatedUtc)
                .Select(x => new ReviewViewListItem { Issue = x }).ToList();
            var root = new DockPanel { Margin = new Thickness(14) };
            Content = root;

            var header = new StackPanel();
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);
            header.Children.Add(new TextBlock
            {
                Text = "ISOLATED MANHOLES / REVIEW QUEUE",
                FontSize = 19,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 8)
            });
            header.Children.Add(new TextBlock
            {
                Text = "Select rows to generate/update their individual 3D section-box views. " +
                    "Unselected and resolved issues are kept in the register.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            });
            header.Children.Add(new TextBlock
            {
                Text = "Saved register: " + registerPath,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            });

            var actions = new WrapPanel();
            header.Children.Add(actions);
            var all = new Button
            {
                Content = "Select All",
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(0, 0, 8, 0)
            };
            var none = new Button
            {
                Content = "Clear Selection",
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(0, 0, 15, 0)
            };
            actions.Children.Add(all);
            actions.Children.Add(none);
            actions.Children.Add(new TextBlock
            {
                Text = "3D Section Box margin (mm): ",
                VerticalAlignment = VerticalAlignment.Center
            });
            _margin = new TextBox
            {
                Text = "350",
                Width = 76,
                Margin = new Thickness(3, 0, 0, 0),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            actions.Children.Add(_margin);

            var footer = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);
            var markResolved = new Button
            {
                Content = "Mark Selected Resolved",
                MinWidth = 165,
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 0, 12, 0)
            };
            var reopen = new Button
            {
                Content = "Reopen Selected",
                MinWidth = 135,
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 0, 12, 0)
            };
            var cancel = new Button
            {
                Content = "Close",
                MinWidth = 95,
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 0, 12, 0)
            };
            var create = new Button
            {
                Content = "Create / Update 3D Views",
                MinWidth = 210,
                Padding = new Thickness(8, 6, 8, 6)
            };
            footer.Children.Add(markResolved);
            footer.Children.Add(reopen);
            footer.Children.Add(cancel);
            footer.Children.Add(create);
            cancel.Click += (s, e) => DialogResult = false;

            var grid = new DataGrid
            {
                AutoGenerateColumns = false,
                ItemsSource = _items,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                IsReadOnly = false,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            root.Children.Add(grid);
            var selectColumn = new DataGridCheckBoxColumn
            {
                Header = "3D",
                Binding = new System.Windows.Data.Binding("Selected")
                {
                    Mode = System.Windows.Data.BindingMode.TwoWay,
                    UpdateSourceTrigger =
                        System.Windows.Data.UpdateSourceTrigger.PropertyChanged
                },
                Width = 52
            };
            grid.Columns.Add(selectColumn);
            Column(grid, "Foundation", "FoundationId", 96);
            Column(grid, "Status", "Status", 85);
            Column(grid, "Severity", "Severity", 100);
            Column(grid, "Problem / review reason", "Reason", 440);
            Column(grid, "Walls", "Walls", 200);
            Column(grid, "Existing 3D view", "View", 190);
            Column(grid, "Last detection (UTC)", "LastDetected", 190);
            foreach (DataGridColumn col in grid.Columns.Skip(1))
                col.IsReadOnly = true;

            all.Click += (s, e) =>
            {
                foreach (ReviewViewListItem item in _items) item.Selected = true;
                grid.Items.Refresh();
            };
            none.Click += (s, e) =>
            {
                foreach (ReviewViewListItem item in _items) item.Selected = false;
                grid.Items.Refresh();
            };

            Action<string> setStatus = status =>
            {
                grid.CommitEdit(DataGridEditingUnit.Cell, true);
                grid.CommitEdit(DataGridEditingUnit.Row, true);
                List<ReviewViewListItem> chosen = _items.Where(x =>
                    x.Selected).ToList();
                if (chosen.Count == 0)
                {
                    MessageBox.Show(this, "Select at least one row.");
                    return;
                }
                if (status == "RESOLVED")
                {
                    MessageBoxResult confirmation = MessageBox.Show(this,
                        "Mark " + chosen.Count + " selected issue(s) as resolved? " +
                        "Experimental Batch All will no longer skip those foundations.",
                        "Mark Resolved", MessageBoxButton.YesNo);
                    if (confirmation != MessageBoxResult.Yes) return;
                }
                foreach (ReviewViewListItem item in chosen)
                {
                    item.Issue.Status = status;
                    item.Issue.UpdatedUtc = DateTime.UtcNow.ToString("O");
                }
                StatusModified = true;
                CreateViews = false;
                DialogResult = true;
            };
            markResolved.Click += (s, e) => setStatus("RESOLVED");
            reopen.Click += (s, e) => setStatus("OPEN");

            create.Click += (s, e) =>
            {
                grid.CommitEdit(DataGridEditingUnit.Cell, true);
                grid.CommitEdit(DataGridEditingUnit.Row, true);
                double margin;
                if ((!double.TryParse(_margin.Text,
                    NumberStyles.Float, CultureInfo.CurrentCulture,
                    out margin) &&
                    !double.TryParse(_margin.Text, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out margin)) ||
                    margin < 50 || margin > 2000 ||
                    double.IsNaN(margin) || double.IsInfinity(margin))
                {
                    MessageBox.Show(this,
                        "Enter a 3D margin from 50 to 2000 mm.");
                    return;
                }
                SelectedIssues = _items.Where(x => x.Selected)
                    .Select(x => x.Issue).ToList();
                if (SelectedIssues.Count == 0)
                {
                    MessageBox.Show(this, "Select at least one manhole.");
                    return;
                }
                MarginMm = margin;
                CreateViews = true;
                DialogResult = true;
            };
        }

        private static void Column(DataGrid grid, string label,
            string field, double width)
        {
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = label,
                Binding = new System.Windows.Data.Binding(field),
                Width = new DataGridLength(width)
            });
        }
    }
}
