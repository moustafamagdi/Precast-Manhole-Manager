using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.DB;
using Hatco.PrecastManholeManager.Infrastructure;

namespace Hatco.PrecastManholeManager.Services
{
    internal sealed class OpeningResetAuditResult
    {
        public int NativeManaged { get; set; }
        public int NativeUnmanaged { get; set; }
        public int ProfileEditedWalls { get; set; }
        public int ProfileUnknownWalls { get; set; }
        public int VoidCutRelations { get; set; }
        public int VoidUnknownWalls { get; set; }
        public bool RequiresManualReview =>
            NativeUnmanaged > 0 || ProfileEditedWalls > 0 ||
            ProfileUnknownWalls > 0 || VoidCutRelations > 0 || VoidUnknownWalls > 0;
    }

    // Inventory only. Reset Profile would destroy arbitrary profile changes and
    // deleting in-place voids could impact unrelated elements; never mutate here.
    internal static class OpeningResetAuditService
    {
        public static OpeningResetAuditResult Audit(
            Document doc, IEnumerable<Wall> walls, DiagnosticLogger log)
        {
            var result = new OpeningResetAuditResult();
            List<Wall> wallList = (walls ?? Enumerable.Empty<Wall>())
                .Where(w => w != null)
                .GroupBy(w => w.Id.IntegerValue)
                .Select(g => g.First()).ToList();
            var ids = new HashSet<int>(wallList.Select(w => w.Id.IntegerValue));

            log?.WriteHeader("EXPERIMENTAL OPENING RESET AUDIT (NO DELETION)");
            foreach (Wall wall in wallList)
            {
                string profile = ReadSketchStatus(wall);
                if (profile == "EDITED PROFILE") result.ProfileEditedWalls++;
                if (profile == "UNKNOWN") result.ProfileUnknownWalls++;

                int cuts = ReadVoidCutCount(doc, wall);
                if (cuts < 0) result.VoidUnknownWalls++;
                else result.VoidCutRelations += cuts;

                log?.Info("WALL_AUDIT Wall=" + wall.Id.IntegerValue +
                          " Profile=" + profile +
                          " UnattachedVoidCutCount=" + (cuts < 0 ? "UNKNOWN" : cuts.ToString()));
            }

            foreach (Opening opening in new FilteredElementCollector(doc)
                .OfClass(typeof(Opening)).Cast<Opening>())
            {
                if (opening.Host == null ||
                    !ids.Contains(opening.Host.Id.IntegerValue)) continue;
                ManagedOpeningData managed;
                if (OpeningStorageService.TryRead(opening, out managed))
                {
                    result.NativeManaged++;
                    log?.Info("NATIVE_MANAGED Opening=" + opening.Id.IntegerValue +
                              " Wall=" + opening.Host.Id.IntegerValue);
                }
                else
                {
                    result.NativeUnmanaged++;
                    log?.Warn("NATIVE_MANUAL Opening=" + opening.Id.IntegerValue +
                              " Wall=" + opening.Host.Id.IntegerValue +
                              " (preserve until explicitly reviewed)");
                }
            }

            log?.Info("RESET_AUDIT SUMMARY Managed=" + result.NativeManaged +
                      " NativeManual=" + result.NativeUnmanaged +
                      " ProfileEdited=" + result.ProfileEditedWalls +
                      " ProfileUnknown=" + result.ProfileUnknownWalls +
                      " VoidCutRelations=" + result.VoidCutRelations +
                      " VoidUnknown=" + result.VoidUnknownWalls);
            log?.Info("No openings, sketch profiles, void elements or cut relations were changed.");
            return result;
        }

        private static string ReadSketchStatus(Wall wall)
        {
            // Reflection keeps this diagnostic build tolerant of API versions where
            // the SketchId member or profile editing representation differs.
            try
            {
                PropertyInfo property = wall.GetType().GetProperty("SketchId",
                    BindingFlags.Public | BindingFlags.Instance);
                if (property == null) return "UNKNOWN";
                ElementId id = property.GetValue(wall, null) as ElementId;
                if (id == null || id == ElementId.InvalidElementId)
                    return "NO EDITED SKETCH";
                return "EDITED PROFILE";
            }
            catch { return "UNKNOWN"; }
        }

        private static int ReadVoidCutCount(Document doc, Wall wall)
        {
            try
            {
                MethodInfo method = typeof(InstanceVoidCutUtils).GetMethod(
                    "GetCuttingVoidInstances",
                    BindingFlags.Public | BindingFlags.Static,
                    null, new[] { typeof(Element) }, null);
                if (method == null) return -1;
                object value = method.Invoke(null, new object[] { wall });
                var enumerable = value as IEnumerable;
                if (enumerable == null) return -1;
                int count = 0;
                foreach (object item in enumerable)
                {
                    count++;
                    if (item == null) continue;
                    // Exact instance type can vary; raw count is enough for audit.
                }
                return count;
            }
            catch { return -1; }
        }
    }
}
