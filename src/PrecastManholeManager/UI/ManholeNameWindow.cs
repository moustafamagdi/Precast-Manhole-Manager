using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Hatco.PrecastManholeManager.UI
{
    // A single explicit request for a site-readable designation.
    // It does not save any Revit data; the caller commits the accepted
    // name only together with a successful production manhole.
    internal sealed class ManholeNameWindow : Window
    {
        private readonly TextBox _name;
        public string ManholeName { get; private set; }

        public ManholeNameWindow(int foundationId)
        {
            Title = "First production manhole - designation";
            Width = 450;
            Height = 220;
            MinWidth = 380;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            var panel = new StackPanel { Margin = new Thickness(18) };
            Content = panel;
            panel.Children.Add(new TextBlock
            {
                Text = "Enter the real manhole name/number shown on the " +
                    "site drawings (for example: MH-01 or DRAIN-MH-04).",
                TextWrapping = TextWrapping.Wrap
            });
            panel.Children.Add(new TextBlock
            {
                Text = "Selected Revit foundation ID: " + foundationId +
                    " (not used as the drawing number)",
                Margin = new Thickness(0, 7, 0, 8)
            });
            _name = new TextBox { MaxLength = 80, Height = 29 };
            panel.Children.Add(_name);

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0)
            };
            panel.Children.Add(actions);
            var cancel = new Button { Content = "Cancel", Width = 95,
                Margin = new Thickness(0, 0, 10, 0), IsCancel = true };
            var accept = new Button { Content = "Use this name",
                Width = 125, IsDefault = true };
            actions.Children.Add(cancel);
            actions.Children.Add(accept);
            accept.Click += (sender, args) =>
            {
                string entered = (_name.Text ?? "").Trim();
                if (entered.Length < 2)
                {
                    MessageBox.Show(this,
                        "Enter at least two characters for the manhole name.");
                    return;
                }
                ManholeName = entered;
                DialogResult = true;
            };
            Loaded += (sender, args) =>
            {
                _name.Focus();
                Keyboard.Focus(_name);
            };
        }
    }
}