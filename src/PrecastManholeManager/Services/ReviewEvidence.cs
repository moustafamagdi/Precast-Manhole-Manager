using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;

namespace Hatco.PrecastManholeManager.Services
{
    internal static class ReviewEvidence
    {
        internal static string ForDomain(Document doc, Element foundation, IEnumerable<int> walls, ReviewDomain domain)
        {
            var elements = walls.Select(id => doc.GetElement(new ElementId(id))).Where(x => x != null).Concat(new[] { foundation }).ToList();
            if (domain == ReviewDomain.Dimensions || domain == ReviewDomain.Views || domain == ReviewDomain.Layout || domain == ReviewDomain.Presentation)
            {
                string prefix = "MH_" + foundation.Id.IntegerValue + "_";
                var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                    .Where(v => !v.IsTemplate && v.Name.StartsWith(prefix, StringComparison.Ordinal)).ToList();
                elements.AddRange(views);
                elements.AddRange(views.Select(v => doc.GetElement(v.ViewTemplateId)).Where(x => x != null));
            }
            return string.Join("|", elements.OrderBy(x => x.UniqueId).Select(x => x.UniqueId + ":" + x.VersionGuid));
        }
        // Acceptance applies to the observed geometry, not just a reusable error message.
        internal static string Capture(Document doc, Element foundation, IEnumerable<Wall> walls,
            double clearance, IEnumerable<UnifiedOpeningReviewRow> sources = null)
        {
            var parts = new List<string> { clearance.ToString("R", CultureInfo.InvariantCulture) };
            foreach (var element in walls.Cast<Element>().Concat(new[] { foundation }).OrderBy(x => x.UniqueId))
            {
                parts.Add(element.UniqueId + ":" + element.VersionGuid);
                var box = element.get_BoundingBox(null);
                if (box != null) { parts.Add(Point(box.Min)); parts.Add(Point(box.Max)); }
            }
            foreach (var row in (sources ?? Enumerable.Empty<UnifiedOpeningReviewRow>()).OrderBy(x => x.Source.SourceKey))
            {
                var source = row.Source;
                var link = doc.GetElement(new ElementId(source.LinkInstanceId)) as RevitLinkInstance;
                var element = link?.GetLinkDocument()?.GetElement(new ElementId(source.LinkedElementId));
                parts.Add(source.SourceKey + ":" + element?.VersionGuid + ":" + link?.VersionGuid);
                parts.Add(Point(new XYZ(source.EffectiveOpeningXmm, source.EffectiveOpeningYmm, source.EffectiveOpeningZmm)));
                parts.Add(source.CutWidthMm.ToString("R", CultureInfo.InvariantCulture) + ":" + source.CutHeightMm.ToString("R", CultureInfo.InvariantCulture));
            }
            using (var hash = SHA256.Create())
                return Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(string.Join("|", parts))));
        }

        private static string Point(XYZ p) => string.Join(",", new[] { p.X, p.Y, p.Z }.Select(x => x.ToString("R", CultureInfo.InvariantCulture)));
    }
}
