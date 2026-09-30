using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Hatco.PrecastManholeManager.UI
{
    internal sealed class BatchProgressWindow : Window
    {
        private readonly TextBlock status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        private readonly ProgressBar progress = new ProgressBar { Height = 20, Margin = new Thickness(0, 15, 0, 15) };
        private readonly IntPtr owner;
        private bool finished;
        public bool CancelRequested { get; private set; }
        [DllImport("user32.dll")] private static extern bool EnableWindow(IntPtr handle, bool enabled);
        [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint flags);
        public BatchProgressWindow(IntPtr owner)
        {
            this.owner = owner;
            Title = "Precast Manholes - Unattended Run"; Width = 570; Height = 240;
            ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
            new WindowInteropHelper(this).Owner = owner;
            var panel = new StackPanel { Margin = new Thickness(20) };
            Content = panel; panel.Children.Add(status); panel.Children.Add(progress);
            var cancel = new Button { Content = "Stop after current manhole and save", Padding = new Thickness(8) };
            cancel.Click += (s,e) => { CancelRequested = true; cancel.IsEnabled = false; };
            panel.Children.Add(cancel);
            Closing += (s,e) => { if (!finished) { CancelRequested = true; e.Cancel = true; } };
        }
        public void Start() { Show(); EnableWindow(owner, false); SetThreadExecutionState(0x80000001); }
        public void Update(int done, int total, string text)
        {
            status.Text = done + " / " + total + "\n" + text;
            progress.Maximum = Math.Max(1,total); progress.Value = done;
            var frame = new DispatcherFrame();
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(()=>frame.Continue=false));
            Dispatcher.PushFrame(frame);
        }
        public void Finish()
        {
            finished = true; EnableWindow(owner, true); SetThreadExecutionState(0x80000000); Close();
        }
    }
}
