using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Hatco.PrecastManholeManager.Models;

namespace Hatco.PrecastManholeManager.Services
{
    // Bounds already include clearance. A merged cut is their enclosing rectangle,
    // not a larger pipe diameter and not a second application of clearance.
    internal static class CompoundOpeningService
    {
        private static readonly Guid SchemaId = new Guid("2D166AC7-62CF-4EC6-A90E-E43C8ACD3390");

        internal static string[] Members(PenetrationRecord r) => r.MemberSourceKeys ?? new[] { r.SourceKey };

        internal static bool AuthorizeReplacement(string[] members, IEnumerable<string> observed, bool merge)
        {
            if (members == null || members.Length == 0 || members.Any(string.IsNullOrWhiteSpace))
                throw new InvalidOperationException("Existing opening has invalid member identities.");
            var keys = new HashSet<string>(observed, StringComparer.Ordinal);
            bool shared = members.Any(keys.Contains);
            if (shared && !members.All(keys.Contains))
                throw new InvalidOperationException("Combined opening has missing/unloaded sources; preserved for review.");
            if (shared && members.Length > 1 && !merge)
                throw new InvalidOperationException("Enable Merge overlapping openings to update an existing combined opening.");
            return shared;
        }

        internal static bool Overlaps(PenetrationRecord a, PenetrationRecord b, double dx, double dy, double gap = 5)
        {
            return a.HostWallId == b.HostWallId &&
                Math.Abs((a.EffectiveOpeningXmm - b.EffectiveOpeningXmm) * dx +
                         (a.EffectiveOpeningYmm - b.EffectiveOpeningYmm) * dy) < (a.CutWidthMm + b.CutWidthMm) / 2 + gap &&
                Math.Abs(a.EffectiveOpeningZmm - b.EffectiveOpeningZmm) < (a.CutHeightMm + b.CutHeightMm) / 2 + gap;
        }

        internal static List<PenetrationRecord> Combine(IEnumerable<PenetrationRecord> sources, double dx, double dy, bool enabled)
        {
            var result = sources.OrderBy(r => r.SourceKey, StringComparer.Ordinal).ToList();
            if (result.Select(r => r.SourceKey).Distinct().Count() != result.Count)
                throw new InvalidOperationException("Duplicate source keys on wall.");
            if (!Finite(dx) || !Finite(dy) || Math.Abs(dx * dx + dy * dy - 1) > 0.0001)
                throw new InvalidOperationException("Invalid horizontal wall direction.");
            if (result.Any(r => !Finite(r.CutWidthMm) || !Finite(r.CutHeightMm) || r.CutWidthMm <= 0 || r.CutHeightMm <= 0 ||
                !Finite(r.EffectiveOpeningXmm) || !Finite(r.EffectiveOpeningYmm) || !Finite(r.EffectiveOpeningZmm)))
                throw new InvalidOperationException("Unresolved opening envelope.");
            // Restart after each union: the new rectangle may overlap another cut
            // even when the original two rectangles did not touch that third cut.
            for (int i = 0; i < result.Count; i++)
            for (int j = i + 1; j < result.Count; j++)
            {
                if (!Overlaps(result[i], result[j], dx, dy)) continue;
                if (!enabled)
                    throw new InvalidOperationException("Overlapping/less than 5 mm apart openings on W" + result[i].WallNumber +
                        "; enable Merge overlapping openings to use one rectangular cut.");
                result[i] = Union(result[i], result[j], dx, dy);
                result.RemoveAt(j);
                i = -1;
                break;
            }
            return result;
        }

        internal static List<List<PenetrationRecord>> Partition(IEnumerable<PenetrationRecord> sources,
            double dx, double dy, IEnumerable<string[]> existingGroups)
        {
            var groups = sources.OrderBy(r => r.SourceKey, StringComparer.Ordinal).Select(r => new List<PenetrationRecord> { r }).ToList();
            var old = existingGroups.ToList();
            for (int i = 0; i < groups.Count; i++)
            for (int j = i + 1; j < groups.Count; j++)
            {
                var a = groups[i].Aggregate((x, y) => Union(x, y, dx, dy));
                var b = groups[j].Aggregate((x, y) => Union(x, y, dx, dy));
                bool sharedOldCut = old.Any(keys => groups[i].Any(r => keys.Contains(r.SourceKey)) && groups[j].Any(r => keys.Contains(r.SourceKey)));
                if (!Overlaps(a, b, dx, dy) && !sharedOldCut) continue;
                groups[i].AddRange(groups[j]); groups.RemoveAt(j); i = -1; break;
            }
            return groups;
        }

        private static bool Finite(double n) => !double.IsNaN(n) && !double.IsInfinity(n);

        internal static bool Contains(PenetrationRecord outer, PenetrationRecord inner, double dx, double dy)
        {
            return outer.HostWallId == inner.HostWallId &&
                Math.Abs((outer.EffectiveOpeningXmm - inner.EffectiveOpeningXmm) * dx +
                    (outer.EffectiveOpeningYmm - inner.EffectiveOpeningYmm) * dy) + inner.CutWidthMm / 2 <= outer.CutWidthMm / 2 + 0.5 &&
                Math.Abs(outer.EffectiveOpeningZmm - inner.EffectiveOpeningZmm) + inner.CutHeightMm / 2 <= outer.CutHeightMm / 2 + 0.5;
        }

        internal static bool ExistingCutCoversPair(Document doc, CleanSyncPlan plan, PenetrationRecord a, PenetrationRecord b, XYZ direction)
        {
            foreach (int id in plan.ManagedOpeningIds.Values)
            {
                var opening = doc.GetElement(new ElementId(id)) as Opening;
                if (opening == null || opening.Host?.Id.IntegerValue != a.HostWallId || !opening.IsRectBoundary) continue;
                ManagedOpeningData data;
                if (!OpeningStorageService.TryRead(opening, out data) || data.AdoptedManual || data.HostWallId != a.HostWallId) continue;
                string[] members;
                try { members = ReadMembers(opening, data.SourceKey); }
                catch { continue; }
                if (!members.Contains(a.SourceKey) || !members.Contains(b.SourceKey)) continue;
                var corners = opening.BoundaryRect;
                if (corners.Count != 2) continue;
                var center = (corners[0] + corners[1]) / 2;
                var delta = corners[1] - corners[0];
                var actual = new PenetrationRecord { HostWallId = data.HostWallId,
                    CornerStartAllowed = a.CornerStartAllowed || b.CornerStartAllowed, CornerEndAllowed = a.CornerEndAllowed || b.CornerEndAllowed,
                    Xmm = UnitUtil.FtToMm(center.X), Ymm = UnitUtil.FtToMm(center.Y), Zmm = UnitUtil.FtToMm(center.Z),
                    CutWidthOverrideMm = UnitUtil.FtToMm(Math.Abs(delta.DotProduct(direction))),
                    CutHeightOverrideMm = UnitUtil.FtToMm(Math.Abs(delta.Z)) };
                string why;
                if (Contains(actual, a, direction.X, direction.Y) && Contains(actual, b, direction.X, direction.Y) &&
                    OpeningFitValidationService.TryValidate(doc, actual, out why)) return true;
            }
            return false;
        }

        private static PenetrationRecord Union(PenetrationRecord a, PenetrationRecord b, double dx, double dy)
        {
            double ac = a.EffectiveOpeningXmm * dx + a.EffectiveOpeningYmm * dy;
            double bc = b.EffectiveOpeningXmm * dx + b.EffectiveOpeningYmm * dy;
            double left = Math.Min(ac - a.CutWidthMm / 2, bc - b.CutWidthMm / 2);
            double right = Math.Max(ac + a.CutWidthMm / 2, bc + b.CutWidthMm / 2);
            double bottom = Math.Min(a.EffectiveOpeningZmm - a.CutHeightMm / 2, b.EffectiveOpeningZmm - b.CutHeightMm / 2);
            double top = Math.Max(a.EffectiveOpeningZmm + a.CutHeightMm / 2, b.EffectiveOpeningZmm + b.CutHeightMm / 2);
            var members = Members(a).Concat(Members(b)).Distinct().OrderBy(k => k, StringComparer.Ordinal).ToArray();
            string hash;
            using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", members)))).Replace("-", "");
            return new PenetrationRecord {
                HostWallId = a.HostWallId, WallNumber = a.WallNumber, Shape = "Rectangular",
                CornerStartAllowed = a.CornerStartAllowed || b.CornerStartAllowed,
                CornerEndAllowed = a.CornerEndAllowed || b.CornerEndAllowed,
                LinkInstanceId = a.LinkInstanceId, LinkName = "Combined sources", LinkedElementId = a.LinkedElementId,
                LinkedUniqueId = hash, SourceKeyOverride = "GROUP|" + a.HostWallId + "|" + hash, MemberSourceKeys = members,
                Category = "Combined Pipe/Duct", Notes = string.Join("; ", members), ClearanceMm = a.ClearanceMm,
                Xmm = a.EffectiveOpeningXmm + ((left + right) / 2 - ac) * dx,
                Ymm = a.EffectiveOpeningYmm + ((left + right) / 2 - ac) * dy, Zmm = (bottom + top) / 2,
                CutWidthOverrideMm = right - left, CutHeightOverrideMm = top - bottom,
                WidthMm = right - left - 2 * a.ClearanceMm, HeightMm = top - bottom - 2 * a.ClearanceMm
            };
        }

        internal static void WriteMembers(Opening opening, PenetrationRecord record)
        {
            if (record.MemberSourceKeys == null) return;
            var schema = Schema.Lookup(SchemaId);
            if (schema == null)
            {
                var builder = new SchemaBuilder(SchemaId);
                builder.SetSchemaName("HatcoCombinedOpeningSources");
                builder.AddSimpleField("Members", typeof(string));
                schema = builder.Finish();
            }
            var entity = new Entity(schema);
            entity.Set(schema.GetField("Members"), string.Join("\n", record.MemberSourceKeys));
            opening.SetEntity(entity);
        }

        internal static string[] ReadMembers(Opening opening, string sourceKey)
        {
            var schema = Schema.Lookup(SchemaId);
            if (schema != null)
            {
                var entity = opening.GetEntity(schema);
                if (entity.IsValid()) return entity.Get<string>(schema.GetField("Members")).Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            }
            if (sourceKey.StartsWith("GROUP|", StringComparison.Ordinal))
                throw new InvalidOperationException("Combined opening member identities missing; preserve it for review.");
            return new[] { sourceKey };
        }
    }
}
