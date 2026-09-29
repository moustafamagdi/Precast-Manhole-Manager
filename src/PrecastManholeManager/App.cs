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
                catch { }

                RibbonPanel panel = application.GetRibbonPanels(TabName)
                    .FirstOrDefault(p => p.Name.Equals(PanelName, StringComparison.OrdinalIgnoreCase))
                    ?? application.CreateRibbonPanel(TabName, PanelName);

                string assemblyPath = Assembly.GetExecutingAssembly().Location;

                AddButton(
                    panel,
                    new PushButtonData(
                        "PrecastManholeScan",
                        "Scan\nManhole",
                        assemblyPath,
                        typeof(Commands.ScanManholeCommand).FullName)
                    {
                        ToolTip = "Scan one manhole, review penetrations, sync openings, save data, and export manufacturer data."
                    });

                AddButton(
                    panel,
                    new PushButtonData(
                        "PrecastManholeBatchSelected",
                        "Batch\nSelected",
                        assemblyPath,
                        typeof(Commands.BatchSelectedManholesCommand).FullName)
                    {
                        ToolTip = "Process multiple selected manhole Structural Foundations in one run."
                    });

                AddButton(
                    panel,
                    new PushButtonData(
                        "PrecastManholeBatchAll",
                        "Batch\nAll",
                        assemblyPath,
                        typeof(Commands.BatchAllManholesCommand).FullName)
                    {
                        ToolTip = "Find likely manhole Structural Foundations by MH / MANHOLE naming and process them in one run."
                    });

                return Result.Succeeded;
            }
            catch
            {
                return Result.Failed;
            }
        }

        private static void AddButton(RibbonPanel panel, PushButtonData data)
        {
            if (panel.GetItems().All(i => i.Name != data.Name))
                panel.AddItem(data);
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            return Result.Succeeded;
        }
    }
}
