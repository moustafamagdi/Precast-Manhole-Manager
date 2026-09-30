using System;
using Autodesk.Revit.DB;
using Hatco.PrecastManholeManager.Infrastructure;

namespace Hatco.PrecastManholeManager.Services
{
    internal static class ManholeViewTitleService
    {
        internal static string Name(Document doc, Element foundation, DiagnosticLogger log)
        {
            var saved = ManholeDataCarrierService.ReadForFoundation(
                doc, foundation.UniqueId, foundation.Id.IntegerValue);
            string name = (saved?.ManholeNumber ?? "").Trim();
            if (string.IsNullOrWhiteSpace(name))
                name = (ManholeIdentityStore.Read(foundation) ?? "").Trim();
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
                !existing.Equals(oldAuto, StringComparison.OrdinalIgnoreCase))
                return;
            string title = manholeName + (wallNumber == 0
                ? " - PLAN"
                : " - WALL W" + wallNumber);
            if (existing != title) p.Set(title);
        }
    }
}