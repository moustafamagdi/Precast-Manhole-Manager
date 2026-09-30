using System;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.UI;

namespace Hatco.PrecastManholeManager
{
    // One-time project utility: ONE ribbon button for normal users.
    // Prior diagnostic commands remain compiled and callable through
    // AddInManager while the main interface is being validated.
    public sealed class App : IExternalApplication
    {
        private const string TabName = "Hatco";
        private const string PanelName = "Precast Tools";

        public Result OnStartup(UIControlledApplication app)
        {
            try
            {
                try { app.CreateRibbonTab(TabName); }
                catch { }

                RibbonPanel panel = app.GetRibbonPanels(TabName)
                    .FirstOrDefault(p => p.Name.Equals(PanelName,
                        StringComparison.OrdinalIgnoreCase))
                    ?? app.CreateRibbonPanel(TabName, PanelName);

                string dll = Assembly.GetExecutingAssembly().Location;
                if (panel.GetItems().All(x =>
                    x.Name != "PrecastProjectRunner"))
                {
                    panel.AddItem(new PushButtonData(
                        "PrecastProjectRunner",
                        "Precast\nManholes",
                        dll,
                        typeof(Commands.ProjectRunnerCommand).FullName)
                    {
                        ToolTip = "Single project workflow: scan once, review exceptions, create cropped 3D review views, and export previously saved manufacturer records."
                    });
                }
                return Result.Succeeded;
            }
            catch { return Result.Failed; }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            return Result.Succeeded;
        }
    }
}
