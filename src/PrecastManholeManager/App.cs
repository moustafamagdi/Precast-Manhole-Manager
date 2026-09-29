using System;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.UI;

namespace Hatco.PrecastManholeManager
{
    public class App : IExternalApplication
    {
        private const string TabName = "Hatco";
        private const string PanelName = "Precast Tools";

        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                try { application.CreateRibbonTab(TabName); }
                catch { /* tab already exists */ }

                RibbonPanel panel = application.GetRibbonPanels(TabName)
                    .FirstOrDefault(p => p.Name.Equals(PanelName, StringComparison.OrdinalIgnoreCase))
                    ?? application.CreateRibbonPanel(TabName, PanelName);

                string assemblyPath = Assembly.GetExecutingAssembly().Location;
                var buttonData = new PushButtonData(
                    "PrecastManholeScan",
                    "Scan\nManhole",
                    assemblyPath,
                    typeof(Commands.ScanManholeCommand).FullName)
                {
                    ToolTip = "Phase 1 read-only diagnostic scan for a precast manhole and linked MEP penetrations."
                };

                if (panel.GetItems().All(i => i.Name != buttonData.Name))
                    panel.AddItem(buttonData);

                return Result.Succeeded;
            }
            catch
            {
                return Result.Failed;
            }
        }

        public Result OnShutdown(UIControlledApplication application) => Result.Succeeded;
    }
}
