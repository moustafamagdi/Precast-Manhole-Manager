using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace Hatco.PrecastManholeManager.UI
{
    internal enum BatchEdgePolicy
    {
        Review = 0,
        TrimClearanceOnly = 1
    }

    internal sealed class BatchRunOptions
    {
        public double ClearanceMm { get; set; } = 50;
        public bool PreviewOnly { get; set; } = true;
        public bool AuditExistingOpenings { get; set; } = true;
        public BatchEdgePolicy EdgePolicy { get; set; } = BatchEdgePolicy.Review;
    }

    internal sealed class BatchOptionsWindow : Window
    {
        private readonly TextBox _clearance;
        private readonly ComboBox _edge;
        private readonly CheckBox _preview;
        private readonly CheckBox _audit;

        public BatchRunOptions SelectedOptions { get; private set; }

        public BatchOptionsWindow(int count)
        {
            Title = "Experimental Batch Manholes - Settings";
            Width = 520;
            Height = 420;
            MinWidth = 440;
            ResizeMode = ResizeMode.CanResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            var root = new StackPanel { Margin = new Thickness(20) };
            Content = root;
            root.Children.Add(new TextBlock
            {
                Text = "Batch - " + count + " candidate foundation(s)",
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 16)
            });

            root.Children.Add(new TextBlock
            {
                Text = "Opening clearance per side (mm):",
                Margin = new Thickness(0, 0, 0, 4)
            });
            _clearance = new TextBox
            {
                Text = "50",
                Width = 110,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 12)
            };
            root.Children.Add(_clearance);

            root.Children.Add(new TextBlock
            {
                Text = "When clearance would cross a wall edge:",
                Margin = new Thickness(0, 0, 0, 4)
            });
            _edge = new ComboBox
            {
                Width = 420,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 12)
            };
            _edge.Items.Add("Review / leave unchanged (recommended)");
            _edge.Items.Add("Reduce clearance to wall limit only if MEP section still fits");
            _edge.SelectedIndex = 0;
            root.Children.Add(_edge);

            _preview = new CheckBox
            {
                Content = "PREVIEW ONLY (required in experimental build)",
                IsChecked = true,
                IsEnabled = false,
                Margin = new Thickness(0, 0, 0, 8)
            };
            root.Children.Add(_preview);

            _audit = new CheckBox
            {
                Content = "Audit existing native/profile/void openings (no reset)",
                IsChecked = true,
                Margin = new Thickness(0, 0, 0, 8)
            };
            root.Children.Add(_audit);

            root.Children.Add(new TextBlock
            {
                Text = "Safety: profile edits and in-place void cuts are NEVER reset in this version. " +
                       "If the existing geometry is uncertain, the manhole needs manual review. " +
                       "Use this preview to verify geometry and proposed edge trims before write support is enabled.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 14)
            });

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var ok = new Button { Content = "Continue", MinWidth = 95, Padding = new Thickness(10, 5, 10, 5) };
            var cancel = new Button { Content = "Cancel", MinWidth = 80, Margin = new Thickness(10, 0, 0, 0) };
            ok.Click += (sender, args) =>
            {
                double clearance;
                if (!double.TryParse(_clearance.Text, NumberStyles.Float, CultureInfo.CurrentCulture,
                        out clearance) &&
                    !double.TryParse(_clearance.Text, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out clearance))
                {
                    MessageBox.Show(this, "Enter a numeric clearance in millimeters.");
                    return;
                }
                if (double.IsNaN(clearance) || double.IsInfinity(clearance) ||
                    clearance < 0 || clearance > 500)
                {
                    MessageBox.Show(this, "Clearance must be between 0 and 500 mm.");
                    return;
                }
                SelectedOptions = new BatchRunOptions
                {
                    ClearanceMm = clearance,
                    EdgePolicy = _edge.SelectedIndex == 1
                        ? BatchEdgePolicy.TrimClearanceOnly : BatchEdgePolicy.Review,
                    PreviewOnly = true,
                    AuditExistingOpenings = _audit.IsChecked != false
                };
                DialogResult = true;
            };
            cancel.Click += (sender, args) => DialogResult = false;
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            root.Children.Add(buttons);
        }
    }
}
