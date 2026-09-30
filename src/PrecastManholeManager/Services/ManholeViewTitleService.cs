using System;
using System.Linq;
using Autodesk.Revit.DB;
using Hatco.PrecastManholeManager.Infrastructure;

namespace Hatco.PrecastManholeManager.Services
{
    internal static class ManholeViewTitleService
    {
        internal static ViewFamilyType RequiredSectionType(Document doc)
        {
            var type = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>().FirstOrDefault(t => t.ViewFamily == ViewFamily.Section &&
                    t.Name.Equals("Manhole Sec", StringComparison.OrdinalIgnoreCase));
            if (type == null)
                throw new InvalidOperationException("Missing required Section type 'Manhole Sec'. Load it into this RVT before generating or updating manhole sections.");
            return type;
        }

        internal static string Name(Document doc, Element foundation, DiagnosticLogger log)
        {
            string name = (ManholeIdentityStore.Read(foundation) ?? "").Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                var saved = ManholeDataCarrierService.ReadForFoundation(
                    doc, foundation.UniqueId, foundation.Id.IntegerValue);
                name = (saved?.ManholeNumber ?? "").Trim();
            }
            if (string.IsNullOrWhiteSpace(name))
                name = (foundation.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "").Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                foreach (string parameterName in new[] { "Manhole Number", "MH Number", "Manhole No" })
                {
                    name = (foundation.LookupParameter(parameterName)?.AsString() ?? "").Trim();
                    if (!string.IsNullOrWhiteSpace(name)) break;
                }
            }
            if (string.IsNullOrWhiteSpace(name))
            {
                log?.Warn("No manhole identifier on Foundation " + foundation.Id.IntegerValue);
                return "UNNUMBERED MANHOLE";
            }
            return name;
        }

        internal static void UpdateTitle(View view, string manholeName, int wallNumber,
            int foundationId, DiagnosticLogger log)
        {
            UpdateDetailNumber(view, wallNumber, log);
            Parameter p = view.get_Parameter(BuiltInParameter.VIEW_DESCRIPTION);
            if (p == null || p.IsReadOnly)
            {
                log?.Warn("Title on Sheet locked: " + view.Name);
                return;
            }
            string existing = (p.AsString() ?? "").Trim();
            string oldAuto = "MH " + foundationId + " - " +
                (wallNumber == 0 ? "PLAN" : "W" + wallNumber);
            if (existing.Length > 0 &&
                !existing.Equals(oldAuto, StringComparison.OrdinalIgnoreCase) &&
                !existing.Equals(manholeName + " - WALL W" + wallNumber, StringComparison.OrdinalIgnoreCase))
                return;
            string title = manholeName + (wallNumber == 0
                ? " - PLAN"
                : " - W" + wallNumber);
            if (existing != title) p.Set(title);
        }

        internal static void UpdateDetailNumber(View view, int wallNumber, DiagnosticLogger log)
        {
            if (wallNumber < 1 || wallNumber > 4) return;
            var ports = new FilteredElementCollector(view.Document).OfClass(typeof(Viewport))
                .Cast<Viewport>().ToList();
            string number = "W" + wallNumber;
            int foundationId;
            var parts = view.Name.Split('_');
            if (parts.Length > 2 && parts[0] == "MH" && int.TryParse(parts[1], out foundationId))
            {
                Element foundation = view.Document.GetElement(new ElementId(foundationId));
                if (foundation != null && BatchSheetLayoutService.Find(view.Document, foundation) != null)
                    number = ManholeIdentityStore.Read(foundation) + "-W" + wallNumber;
            }
            foreach (Viewport port in ports.Where(p => p.ViewId == view.Id))
            {
                Parameter parameter = port.get_Parameter(BuiltInParameter.VIEWPORT_DETAIL_NUMBER);
                if (parameter == null || parameter.IsReadOnly)
                {
                    log?.Warn("Detail Number not editable: " + view.Name);
                    continue;
                }
                if (parameter.AsString() == number) continue;
                if (ports.Any(p => p.Id != port.Id && p.SheetId == port.SheetId &&
                    string.Equals(p.get_Parameter(BuiltInParameter.VIEWPORT_DETAIL_NUMBER)?.AsString(),
                        number, StringComparison.OrdinalIgnoreCase)))
                {
                    log?.Warn("Detail Number " + number + " already used on sheet " +
                        port.SheetId.IntegerValue + "; preserved existing number for " + view.Name);
                    continue;
                }
                parameter.Set(number);
                log?.Info("SECTION DETAIL NUMBER " + view.Name + " => " + number);
            }
        }

        // Rename only tool-created view titles that are still recognizably
        // automatic. A manually customized Title on Sheet stays untouched,
        // even when its view name has the same tool prefix.
        internal static void RefreshGeneratedTitles(Document doc,
            Element foundation, string newInternalName,
            string previousAutoName, DiagnosticLogger log)
        {
            string[] roots =
            {
                "MH_" + foundation.Id.IntegerValue + "_DRAFT_2D_",
                "MH_" + foundation.Id.IntegerValue + "_PROD_2D_"
            };
            var views = new FilteredElementCollector(doc)
                .OfClass(typeof(View)).Cast<View>()
                .Where(v => !v.IsTemplate &&
                    roots.Any(root => v.Name.StartsWith(root,
                        StringComparison.OrdinalIgnoreCase))).ToList();

            foreach (View view in views)
            {
                int wallNumber;
                if (view.Name.EndsWith("_PLAN",
                    StringComparison.OrdinalIgnoreCase))
                    wallNumber = 0;
                else
                {
                    int pos = view.Name.LastIndexOf("_W",
                        StringComparison.OrdinalIgnoreCase);
                    if (pos < 0 ||
                        !int.TryParse(view.Name.Substring(pos + 2),
                            out wallNumber) ||
                        wallNumber < 1 || wallNumber > 4)
                        continue;
                }
                Parameter titleParameter = view.get_Parameter(
                    BuiltInParameter.VIEW_DESCRIPTION);
                if (titleParameter == null ||
                    titleParameter.IsReadOnly)
                {
                    log.Warn("Internal title not editable on view " +
                        view.Name);
                    continue;
                }
                string old = (titleParameter.AsString() ?? "").Trim();
                string suffix = wallNumber == 0
                    ? " - PLAN" : " - W" + wallNumber;
                string legacySuffix = wallNumber == 0
                    ? " - PLAN" : " - WALL W" + wallNumber;
                string oldNumeric = "MH " + foundation.Id.IntegerValue +
                    (wallNumber == 0
                        ? " - PLAN" : " - W" + wallNumber);
                string previousTitle =
                    (previousAutoName ?? "") + suffix;
                string newTitle = newInternalName + suffix;
                bool automatic =
                    old.Length == 0 ||
                    old.Equals(oldNumeric,
                        StringComparison.OrdinalIgnoreCase) ||
                    old.Equals("UNNUMBERED MANHOLE" + suffix,
                        StringComparison.OrdinalIgnoreCase) ||
                    old.Equals("UNNUMBERED MANHOLE" + legacySuffix,
                        StringComparison.OrdinalIgnoreCase) ||
                    old.Equals(newInternalName + legacySuffix,
                        StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrWhiteSpace(previousAutoName) &&
                     (old.Equals(previousTitle, StringComparison.OrdinalIgnoreCase) ||
                      old.Equals(previousAutoName + legacySuffix, StringComparison.OrdinalIgnoreCase)));
                if (!automatic)
                {
                    log.Info("Preserved manual sheet title: " +
                        view.Name + " = " + old);
                    continue;
                }
                if (old == newTitle) continue;
                titleParameter.Set(newTitle);
                log.Info("Updated auto sheet title: " +
                    view.Name + " => " + newTitle);
            }
        }
    }
}
