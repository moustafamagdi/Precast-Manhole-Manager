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

                AddButton(
                    panel,
                    new PushButtonData(
                        "PrecastManholeExportExcel",
                        "Export\nExcel",
                        assemblyPath,
                        typeof(Commands.ExportManufacturerExcelCommand).FullName)
                    {
                        ToolTip = "Export all saved precast manholes and openings to one manufacturer Excel workbook."
                    });

                AddButton(
                    panel,
                    new PushButtonData(
                        "TestVirtualFoundation",
                        "Test Virtual\nFoundation",
                        assemblyPath,
                        typeof(Commands.TestVirtualFoundationCommand).FullName)
                    {
                        ToolTip = "Experimental read-only recovery of a complete manhole footprint from its four walls. Does not uncut or modify anything."
                    });

                AddButton(
                    panel,
                    new PushButtonData(
                        "TestVirtualMep",
                        "Test Virtual\\nMEP + Audit",
                        assemblyPath,
                        typeof(Commands.TestVirtualMepCommand).FullName)
                    {
                        ToolTip = "Read-only short pipe/duct endpoint extension and existing wall-opening inventory. Creates no openings."
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
