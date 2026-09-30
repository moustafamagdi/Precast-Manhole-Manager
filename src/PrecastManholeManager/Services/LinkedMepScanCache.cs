using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Hatco.PrecastManholeManager.Infrastructure;

namespace Hatco.PrecastManholeManager.Services
{
    // Scoped to one synchronous command run. Never reused after the user can edit/reload links.
    internal sealed class LinkedMepScanCache : IDisposable
    {
        [ThreadStatic] private static LinkedMepScanCache current;
        private readonly LinkedMepScanCache previous;
        private readonly Dictionary<string, List<Entry>> links = new Dictionary<string, List<Entry>>();
        internal sealed class Entry
        {
            public Element Element;
            public Curve Curve;
            public XYZ Min, Max;
        }
        public LinkedMepScanCache() { previous = current; current = this; }
        internal static IDisposable BeginIfNeeded() => current == null ? new LinkedMepScanCache() : null;
        internal static List<Entry> Query(RevitLinkInstance link, XYZ min, XYZ max, DiagnosticLogger log)
        {
            List<Entry> entries;
            string key = link.UniqueId;
            if (current == null || !current.links.TryGetValue(key, out entries))
            {
                entries = new List<Entry>();
                var linked = link.GetLinkDocument();
                var transform = link.GetTotalTransform();
                int skipped = 0;
                var filter = new ElementMulticategoryFilter(new[] {
                    BuiltInCategory.OST_PipeCurves, BuiltInCategory.OST_DuctCurves });
                foreach (Element element in new FilteredElementCollector(linked)
                    .WherePasses(filter).WhereElementIsNotElementType())
                {
                    Curve curve = null;
                    try
                    {
                        var location = element.Location as LocationCurve;
                        if (location?.Curve == null) { skipped++; continue; }
                        curve = location.Curve.CreateTransformed(transform);
                        var points = curve.Tessellate();
                        if (points.Count == 0) { curve.Dispose(); continue; }
                        entries.Add(new Entry { Element = element, Curve = curve,
                            Min = new XYZ(points.Min(p=>p.X), points.Min(p=>p.Y), points.Min(p=>p.Z)),
                            Max = new XYZ(points.Max(p=>p.X), points.Max(p=>p.Y), points.Max(p=>p.Z)) });
                    }
                    catch (Exception ex) { curve?.Dispose(); skipped++; log.Warn("MEP index source " + element.Id.IntegerValue + ": " + ex.Message); }
                }
                entries = entries.OrderBy(e=>e.Min.X).ToList();
                if (current != null) current.links[key] = entries;
                log.Info("MEP INDEX BUILT Link=" + link.Id.IntegerValue + " Curves=" + entries.Count + " Skipped=" + skipped);
            }
            return entries.TakeWhile(e=>e.Min.X <= max.X).Where(e=>
                Overlaps(e.Min.X,e.Max.X,min.X,max.X) && Overlaps(e.Min.Y,e.Max.Y,min.Y,max.Y) &&
                Overlaps(e.Min.Z,e.Max.Z,min.Z,max.Z)).ToList();
        }
        internal static bool Overlaps(double minA, double maxA, double minB, double maxB) =>
            minA <= maxB && maxA >= minB;
        public void Dispose()
        {
            foreach (var entry in links.Values.SelectMany(x=>x)) entry.Curve.Dispose();
            links.Clear(); current = previous;
        }
    }
}
