using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Hatco.PrecastManholeManager.Services;

namespace Hatco.PrecastManholeManager.UI
{
    internal sealed class CleanSyncConfirmationWindow : Window
    {
        private readonly CheckBox _profiles;
        private readonly CheckBox _manual;
        private readonly CheckBox _voidCuts;
        private readonly CheckBox _inPlace;
        private readonly CheckBox _virtual;
        private readonly CheckBox _apply;
        private readonly CheckBox _linksVerified;
        private readonly TextBox _confirmation;

        public CleanSyncApplyOptions ApplyOptions { get; private set; }

        public CleanSyncConfirmationWindow(CleanSyncPlan plan,
            UnifiedOpeningReviewResult review, string logPath)
        {
            Title = "TEST Clean & Sync - ONE MANHOLE";
            Width = 660;
            Height = 670;
            MinWidth = 560;
            ResizeMode = ResizeMode.CanResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            Content = scroll;
            var root = new StackPanel { Margin = new Thickness(18) };
            scroll.Content = root;
            root.Children.Add(new TextBlock
            {
                Text = "CLEAN & SYNC - EXPERIMENTAL",
                FontSize = 19,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 10)
            });
            root.Children.Add(Line("Foundation " + plan.FoundationId +
                " | Walls: " + string.Join(", ", plan.WallIds)));
            root.Children.Add(Line("Edited profiles: " + plan.ProfileResetCount +
                " | Unmanaged native openings: " + plan.ManualOpeningIds.Count +
                " | Unattached void relationships: " + plan.VoidCutCount));
            root.Children.Add(Line("Existing tool-managed openings: " +
                plan.ManagedOpeningIds.Count + " | Actual hits: " +
                review.Rows.FindAll(x => !x.IsVirtual).Count +
                " | Virtual candidates: " + plan.VirtualCount));
            root.Children.Add(Line("In-place cutter candidates: " +
                plan.InPlaceCutterCount + " (pinned instances will be unpinned " +
                "before targeted deletion, if separately approved)."));
            root.Children.Add(Line("Sloped/skewed virtual deferred: " +
                plan.SlopedVirtualCount +
                " (will NOT be cut until exact projected envelope is validated)."));

            root.Children.Add(new TextBlock
            {
                Text = "Use a DISPOSABLE COPY of the RVT, never the live model. " +
                    "The operation resets approved wall profiles, removes selected cut " +
                    "relationships (not cutting families), deletes unmanaged native " +
                    "openings, then synchronizes tool-managed openings. All changes " +
                    "for this one manhole roll back together on failure.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 12, 0, 14)
            });

            if (!string.IsNullOrWhiteSpace(plan.BlockReason))
            {
                root.Children.Add(Line("BLOCKED: " + plan.BlockReason));
                root.Children.Add(Line(
                    "Changes are disabled until unclassified geometry is resolved."));
            }

            _profiles = Box(root,
                "RESET " + plan.ProfileResetCount +
                " edited wall profiles (discards ALL sketch changes on these walls)",
                plan.ProfileResetCount > 0);
            _manual = Box(root,
                "DELETE " + plan.ManualOpeningIds.Count +
                " non-tool native openings (managed openings are preserved)",
                plan.ManualOpeningIds.Count > 0);
            _voidCuts = Box(root,
                "REMOVE " + plan.VoidCutCount +
                " unattached void cutting RELATIONSHIPS on selected walls only",
                plan.VoidCutCount > 0);
            _inPlace = Box(root,
                "UNPIN & DELETE " + plan.InPlaceCutterCount +
                " classified in-place cutter INSTANCE(S). Only the selected " +
                "manhole's walls were checked for other hosts; verify the " +
                "cutter does not affect foundations/other non-wall elements.",
                plan.InPlaceCutterCount > 0);
            _virtual = Box(root,
                "Create straight/perpendicular VIRTUAL endpoint cuts (not sloped/skewed)",
                false);
            root.Children.Add(new TextBlock
            {
                Text = "Unmatched tool-managed openings are ALWAYS preserved for review. " +
                    "No automatic stale-opening removal. Unknown solid cuts block the operation.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 10, 0, 8)
            });

            if (plan.UnavailableLinks > 0)
            {
                _linksVerified = Box(root,
                    "I verified all REQUIRED MEP links are loaded; " +
                    plan.UnavailableLinks + " other links remain unavailable " +
                    "(opening data may be incomplete)",
                    true);
            }
            _apply = Box(root, "I am testing on a disposable RVT copy", true);
            root.Children.Add(Line("Type CLEAN to unlock the experimental APPLY button:"));
            _confirmation = new TextBox
            {
                Width = 160,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 4, 0, 12)
            };
            root.Children.Add(_confirmation);
            root.Children.Add(Line("Log: " + logPath));

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0)
            };
            root.Children.Add(buttons);

            var cancel = new Button
            {
                Content = "Preview only / Cancel",
                MinWidth = 165,
                Padding = new Thickness(8, 5, 8, 5),
                Margin = new Thickness(0, 0, 12, 0)
            };
            buttons.Children.Add(cancel);
            cancel.Click += (s, e) => DialogResult = false;

            var apply = new Button
            {
                Content = "TEST APPLY (ONE MANHOLE)",
                MinWidth = 215,
                Padding = new Thickness(8, 5, 8, 5),
                IsEnabled = false
            };
            buttons.Children.Add(apply);

            Action refresh = () =>
            {
                apply.IsEnabled =
                    string.IsNullOrWhiteSpace(plan.BlockReason) &&
                    string.Equals((_confirmation.Text ?? "").Trim(),
                        "CLEAN", StringComparison.Ordinal) &&
                    _apply.IsChecked == true &&
                    (plan.UnavailableLinks == 0 ||
                     (_linksVerified != null && _linksVerified.IsChecked == true)) &&
                    (plan.ProfileResetCount == 0 || _profiles.IsChecked == true) &&
                    (plan.VoidCutCount == 0 || _voidCuts.IsChecked == true) &&
                    (plan.ManualOpeningIds.Count == 0 || _manual.IsChecked == true) &&
                    (plan.InPlaceCutterCount == 0 || _inPlace.IsChecked == true);
            };
            _confirmation.TextChanged += (s, e) => refresh();
            _apply.Checked += (s, e) => refresh();
            _apply.Unchecked += (s, e) => refresh();
            if (_linksVerified != null)
            {
                _linksVerified.Checked += (s, e) => refresh();
                _linksVerified.Unchecked += (s, e) => refresh();
            }
            foreach (CheckBox cb in new[] {
                _profiles, _manual, _voidCuts, _inPlace })
            {
                cb.Checked += (s, e) => refresh();
                cb.Unchecked += (s, e) => refresh();
            }

            apply.Click += (s, e) =>
            {
                refresh();
                if (!apply.IsEnabled) return;
                ApplyOptions = new CleanSyncApplyOptions
                {
                    ResetEditedProfiles = _profiles.IsChecked == true,
                    RemoveManualNative = _manual.IsChecked == true,
                    RemoveVoidCutRelations = _voidCuts.IsChecked == true,
                    DeleteIsolatedInPlaceCutters = _inPlace.IsChecked == true,
                    IncludeStraightVirtual = _virtual.IsChecked == true,
                    RequiredLinksVerified = plan.UnavailableLinks == 0 ||
                        _linksVerified?.IsChecked == true
                };
                DialogResult = true;
            };
        }

        private static TextBlock Line(string text)
        {
            return new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 3)
            };
        }

        private static CheckBox Box(StackPanel root, string text, bool enabled)
        {
            var cb = new CheckBox
            {
                Content = text,
                IsEnabled = enabled,
                IsChecked = false,
                Margin = new Thickness(0, 5, 0, 5)
            };
            root.Children.Add(cb);
            return cb;
        }
    }
}
