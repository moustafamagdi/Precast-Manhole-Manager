using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Hatco.PrecastManholeManager.Services
{
    internal static class OverallPlanLabelPolicy
    {
        internal static int FoundationId(string viewName)
        {
            var match = Regex.Match(viewName ?? "", @"^MH_(\d+)_PROD_2D_(PLAN|OUT_W[1-4])$");
            int id;
            return match.Success && int.TryParse(match.Groups[1].Value, out id) ? id : -1;
        }

        internal static string Label(string name, IEnumerable<string> sheets, int placedViews)
        {
            var actual = sheets.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct()
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
            string location = actual.Count == 0 ? "NOT PLACED" :
                (actual.Count == 1 ? "Sheet: " : "SPLIT - Sheets: ") + string.Join(", ", actual);
            if (actual.Count > 0 && placedViews < 5) location += " (PARTIAL " + placedViews + "/5)";
            return name + "\n" + location;
        }

        internal static string SheetName(string name)
        {
            return Regex.Replace(name ?? "", @"\bPrecast\b", "Cast in site", RegexOptions.IgnoreCase);
        }
    }
}
