using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Hatco.PrecastManholeManager.Infrastructure;
using Hatco.PrecastManholeManager.Models;
using Hatco.PrecastManholeManager.UI;

namespace Hatco.PrecastManholeManager.Services
{
    internal sealed class BatchManholeResult
    {
        public int Selected { get; set; }
        public int Isolated { get; set; }
        public int Valid { get; set; }
        public int NeedsReview { get; set; }
        public int Failed { get; set; }
        public int Penetrations { get; set; }
        public int ManualSufficient { get; set; }
        public int ManualTooSmall { get; set; }
        public int OpeningsCreated { get; set; }
        public int OpeningsUpdated { get; set; }
        public int OpeningsUnchanged { get; set; }
        public int OpeningsRemoved { get; set; }
        public int OpeningReviews { get; set; }
        public int CarriersSaved { get; set; }
        public int OpeningsLinked { get; set; }
        public string LogPath { get; set; }
        public bool PreviewOnly { get; set; }
        public int ProposedCuts { get; set; }
        public int ProposedTrims { get; set; }
        public int ExistingManagedRecords { get; set; }
        public int VirtualMepCandidates { get; set; }

        public override string ToString()
        {
            return
                (PreviewOnly ? "PREVIEW ONLY (NO MODEL CHANGES)\n" : "") +
                "Foundations: " + Selected +
                " | Valid: " + Valid +
                " | Review: " + NeedsReview +
                " | Previously isolated: " + Isolated +
                " | Failed: " + Failed +
                "\nPenetrations: " + Penetrations +
                " | Manual OK: " + ManualSufficient +
                " | Manual Too Small: " + ManualTooSmall +
                "\nProposed cuts: " + ProposedCuts +
                " | Proposed clearance trims: " + ProposedTrims +
                " | Existing managed records: " + ExistingManagedRecords +
                "\nVirtual MEP endpoint candidates (review only): " + VirtualMepCandidates +
                "\nOpenings Created: " + OpeningsCreated +
                " | Updated: " + OpeningsUpdated +
                " | Unchanged: " + OpeningsUnchanged +
                " | Removed: " + OpeningsRemoved +
                " | Opening Review: " + OpeningReviews +
                "\nCarriers Saved: " + CarriersSaved +
                " | Openings Linked: " + OpeningsLinked;
        }
    }

    internal static class BatchManholeProcessor
    {
        public static BatchManholeResult Process(
            Document doc,
            IList<Element> foundations,
            DiagnosticLogger log,
            BatchRunOptions options = null)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));

            var result = new BatchManholeResult
            {
                Selected = foundations?.Count ?? 0,
                LogPath = log?.LogPath,
                PreviewOnly = options?.PreviewOnly == true
            };

            int totalLinks;
            int loadedLinks;
            int loadedMepLinks;
            EvaluateLinkAvailability(doc, out totalLinks, out loadedLinks, out loadedMepLinks);

            log?.Info(
                "Batch link preflight: TotalLinks=" + totalLinks +
                " LoadedLinks=" + loadedLinks +
                " LoadedMEPLinks=" + loadedMepLinks);

            if (loadedMepLinks == 0)
            {
                throw new InvalidOperationException(
                    "Batch preview/process stopped: no loaded Revit link containing Pipes, Ducts, Cable Trays, or Conduits is available. " +
                    "Load the required MEP links before running Batch Selected / Batch All.");
            }

            List<ManholeDataRecord> existingManholes = ManholeDataCarrierService.ReadAll(doc);
            HashSet<string> usedNumbers = new HashSet<string>(
                existingManholes
                    .Select(x => x.ManholeNumber)
                    .Where(x => !string.IsNullOrWhiteSpace(x)),
                StringComparer.OrdinalIgnoreCase);

            int nextNumber = ResolveNextNumber(usedNumbers);

            // Only the experimental Batch All path honors the isolated
            // queue. Legacy Batch Selected remains independent.
            HashSet<string> isolated = new HashSet<string>(
                StringComparer.Ordinal);
            if (options != null)
            {
                try
                {
                    foreach (ManholeReviewIssue issue in
                        ManholeReviewRegistry.Load(doc))
                        if (issue.Status == "OPEN")
                            isolated.Add(issue.FoundationUniqueId);
                    log?.Info("Previously isolated manholes to skip: " +
                        isolated.Count);
                }
                catch (Exception ex)
                {
                    // A broken/unavailable register must never permit an
                    // experimental write to silently bypass isolation.
                    throw new InvalidOperationException(
                        "Cannot read isolated manhole review register.", ex);
                }
            }

            log?.WriteHeader("BATCH MANHOLE PROCESSING");
            log?.Info("Foundations queued: " + result.Selected);

            foreach (Element foundation in foundations ?? new List<Element>())
            {
                log?.WriteHeader("BATCH FOUNDATION " + foundation.Id.IntegerValue);

                try
                {
                    if (options != null && isolated.Contains(
                        foundation.UniqueId))
                    {
                        result.Isolated++;
                        log?.Warn("BATCH ISOLATED SKIP Foundation=" +
                            foundation.Id.IntegerValue +
                            " -- use Review Queue / 3D to inspect.");
                        continue;
                    }

                    ManholeDetectionResult manhole;
                    VirtualFoundationResult virtualFootprint = null;
                    if (options == null)
                    {
                        // Preserve the legacy Batch Selected route until tested separately.
                        manhole = new ManholeDetectionService(doc, log).Detect(foundation);
                    }
                    else
                    {
                        virtualFootprint =
                            new VirtualFoundationRecoveryService(doc, log).Analyze(foundation);
                        if (!virtualFootprint.Accepted)
                        {
                            result.NeedsReview++;
                            ManholeReviewRegistry.Upsert(doc, foundation,
                                "Virtual footprint: " + virtualFootprint.Reason,
                                null, "GEOMETRY", log);
                            log?.Warn("BATCH VIRTUAL REVIEW Foundation=" + foundation.Id.IntegerValue +
                                      " Reason=" + virtualFootprint.Reason);
                            continue;
                        }
                        manhole = FromVirtualFootprint(foundation, virtualFootprint);
                    }

                    if (!manhole.IsValid || !string.IsNullOrWhiteSpace(manhole.Warning))
                    {
                        result.NeedsReview++;
                        if (options != null)
                            ManholeReviewRegistry.Upsert(doc, foundation,
                                "Detection: " + (manhole.Warning ??
                                    "invalid wall footprint"),
                                manhole.Walls.Select(x => x.Wall.Id.IntegerValue),
                                "GEOMETRY", log);
                        log?.Warn(
                            "Foundation " + foundation.Id.IntegerValue +
                            " skipped. Valid=" + manhole.IsValid +
                            " Warning='" + (manhole.Warning ?? string.Empty) + "'.");
                        continue;
                    }

                    if (options?.PreviewOnly == true && options.IncludeVirtualMep &&
                        virtualFootprint != null)
                    {
                        VirtualMepScanResult virtualScan =
                            new VirtualMepExtensionScanner(doc, log)
                                .Scan(virtualFootprint, 150.0, 15.0);
                        result.VirtualMepCandidates += virtualScan.Candidates.Count;
                        log?.Info("BATCH VIRTUAL ENDPOINTS Foundation=" +
                            foundation.Id.IntegerValue +
                            " Candidates=" + virtualScan.Candidates.Count +
                            " CSV=" + virtualScan.CsvPath);
                    }

                    var scanner = new MepPenetrationScanner(doc, log);
                    List<PenetrationRecord> penetrations = scanner.Scan(manhole);

                    ExistingOpeningDetectionService.Apply(
                        doc,
                        manhole.Walls.Select(w => w.Wall.Id.IntegerValue),
                        penetrations,
                        log);

                    if (options != null)
                    {
                        foreach (PenetrationRecord record in penetrations)
                            record.ClearanceMm = options.ClearanceMm;
                    }

                    // Always inventory profile/void/manual openings before any
                    // experimental write; disabling optional preview logging
                    // never bypasses destructive-cut safeguards.
                    OpeningResetAuditResult audit = null;
                    if (options != null && (options.AuditExistingOpenings || !options.PreviewOnly))
                        audit = OpeningResetAuditService.Audit(
                            doc, manhole.Walls.Select(w => w.Wall), log);

                    if (options != null && !options.PreviewOnly && audit != null &&
                        (audit.ProfileEditedWalls > 0 || audit.ProfileUnknownWalls > 0 ||
                         audit.VoidCutRelations > 0 || audit.VoidUnknownWalls > 0))
                    {
                        result.Penetrations += penetrations.Count;
                        result.NeedsReview++;
                        log?.Warn("BATCH MANHOLE REVIEW Foundation=" +
                            foundation.Id.IntegerValue +
                            " has edited/unknown wall profiles or void cuts. " +
                            "Detected MEP intersections are logged, but no cut proposals " +
                            "are counted and no model modifications are made.");
                        continue;
                    }

                    if (options?.PreviewOnly == true && audit != null &&
                        audit.RequiresManualReview)
                    {
                        ManholeReviewRegistry.Upsert(doc, foundation,
                            "Existing cuts require review: EditedProfiles=" +
                                audit.ProfileEditedWalls +
                                " UnknownProfiles=" + audit.ProfileUnknownWalls +
                                " VoidRelations=" + audit.VoidCutRelations +
                                " UnknownVoids=" + audit.VoidUnknownWalls +
                                " ManualNative=" + audit.NativeUnmanaged,
                            manhole.Walls.Select(w => w.Wall.Id.IntegerValue),
                            "REQUIRES CLEANUP", log);
                        log?.Warn("BATCH PREVIEW ONLY: existing manual cuts on this manhole registered for isolated review. Other manholes continue.");
                    }

                    foreach (PenetrationRecord r in penetrations)
                    {
                        if (!r.Accepted || r.ExistingOpeningStatus == "EXISTING SUFFICIENT" ||
                            r.ExistingOpeningStatus == "EXISTING TOO SMALL")
                            continue;

                        if (options?.PreviewOnly == true &&
                            r.ExistingOpeningStatus == "MANAGED")
                        {
                            result.ExistingManagedRecords++;
                            log?.Info("EXISTING MANAGED source=" + r.LinkedElementId +
                                " Wall=" + r.HostWallId +
                                " Geometry changes require managed-sync comparison.");
                            continue;
                        }

                        string fitReason;
                        if (OpeningFitValidationService.TryValidate(doc, r, out fitReason))
                        {
                            result.ProposedCuts++;
                            log?.Info("BATCH CUT PROPOSAL Source=" + r.LinkedElementId +
                                " Wall=" + r.HostWallId +
                                " Size=" + r.CutWidthMm.ToString("0.#") +
                                "x" + r.CutHeightMm.ToString("0.#") +
                                " mm ClearancePerSide=" + r.ClearanceMm.ToString("0.#") +
                                " mm Center=(" + r.Xmm.ToString("0.#") + "," +
                                r.Ymm.ToString("0.#") + "," +
                                r.Zmm.ToString("0.#") + ") mm");
                            continue;
                        }

                        if (options?.EdgePolicy == BatchEdgePolicy.TrimClearanceOnly &&
                            SafeOpeningEdgeService.TryTrimClearance(doc, r, log, out fitReason))
                        {
                            result.ProposedCuts++;
                            result.ProposedTrims++;
                            log?.Warn("BATCH TRIM PROPOSAL Source=" + r.LinkedElementId +
                                " Wall=" + r.HostWallId +
                                " Clearance-only cut=" + r.CutWidthMm.ToString("0.#") +
                                "x" + r.CutHeightMm.ToString("0.#") + " mm.");
                        }
                        else
                        {
                            result.OpeningReviews++;
                            r.Accepted = false;
                            log?.Warn("BATCH OPENING REVIEW Source=" + r.LinkedElementId +
                                      " Wall=" + r.HostWallId + " Reason=" + fitReason);
                        }
                    }

                    result.Penetrations += penetrations.Count;
                    result.ManualSufficient += penetrations.Count(x =>
                        x.ExistingOpeningStatus == "EXISTING SUFFICIENT");
                    result.ManualTooSmall += penetrations.Count(x =>
                        x.ExistingOpeningStatus == "EXISTING TOO SMALL");

                    ManholeDataRecord existing = ManholeDataCarrierService.ReadForFoundation(
                        doc,
                        foundation.UniqueId,
                        foundation.Id.IntegerValue);

                    string manholeNumber = existing?.ManholeNumber;
                    if (string.IsNullOrWhiteSpace(manholeNumber))
                    {
                        manholeNumber = NextAvailableNumber(usedNumbers, ref nextNumber);
                        usedNumbers.Add(manholeNumber);
                    }

                    ManholeDataRecord data = BuildDataRecord(foundation, manhole, manholeNumber);

                    if (options?.PreviewOnly == true)
                    {
                        result.Valid++;
                        log?.Info("BATCH PREVIEW " + manholeNumber +
                            " Foundation=" + foundation.Id.IntegerValue +
                            " Penetrations=" + penetrations.Count +
                            " Proposed cuts this manhole=" +
                            penetrations.Count(x => x.Accepted &&
                              x.ExistingOpeningStatus != "EXISTING SUFFICIENT" &&
                              x.ExistingOpeningStatus != "EXISTING TOO SMALL") +
                            " (NO TRANSACTION / NO CHANGES)");
                        continue;
                    }

                    using (var tx = new Transaction(doc, "HATCO - Batch Precast Manhole " + manholeNumber))
                    {
                        var failurePreprocessor = new OpeningFailurePreprocessor(log);
                        FailureHandlingOptions failureOptions = tx.GetFailureHandlingOptions();
                        failureOptions.SetFailuresPreprocessor(failurePreprocessor);
                        failureOptions.SetClearAfterRollback(true);
                        tx.SetFailureHandlingOptions(failureOptions);

                        tx.Start();

                        BatchOpeningSyncResult sync = BatchOpeningSyncService.Sync(
                            doc,
                            manhole.Walls.Select(w => w.Wall.Id.IntegerValue).ToList(),
                            penetrations,
                            log);

                        DirectShape carrier = ManholeDataCarrierService.CreateOrUpdate(doc, data);

                        int linked = OpeningManholeLinkService.LinkManagedOpenings(
                            doc,
                            data.ManholeNumber,
                            data.FoundationId,
                            new[] { data.Wall1Id, data.Wall2Id, data.Wall3Id, data.Wall4Id });

                        TransactionStatus commitStatus = tx.Commit();

                        if (commitStatus != TransactionStatus.Committed)
                        {
                            result.NeedsReview++;
                            result.OpeningReviews += Math.Max(1, failurePreprocessor.UnresolvedTargetCount);
                            log?.Warn(
                                "BATCH ROLLBACK " + manholeNumber +
                                " Foundation=" + foundation.Id.IntegerValue +
                                " due to unresolved Revit opening/join failure.");
                            continue;
                        }

                        result.Valid++;
                        result.OpeningReviews += failurePreprocessor.ResolvedCount;
                        result.OpeningsCreated += sync.Created;
                        result.OpeningsUpdated += sync.Updated;
                        result.OpeningsUnchanged += sync.Unchanged;
                        result.OpeningsRemoved += sync.Removed;
                        result.OpeningReviews += sync.Review;
                        result.Failed += sync.Failed;
                        result.CarriersSaved++;
                        result.OpeningsLinked += linked;

                        log?.Info(
                            "BATCH OK " + manholeNumber +
                            " Foundation=" + foundation.Id.IntegerValue +
                            " Carrier=" + carrier.Id.IntegerValue +
                            " Penetrations=" + penetrations.Count +
                            " Linked=" + linked +
                            " Created=" + sync.Created +
                            " Updated=" + sync.Updated +
                            " Unchanged=" + sync.Unchanged +
                            " Review=" + sync.Review +
                            " Failed=" + sync.Failed);
                    }
                }
                catch (Exception ex)
                {
                    result.Failed++;
                    if (options != null)
                    {
                        try
                        {
                            ManholeReviewRegistry.Upsert(doc, foundation,
                                "Batch error: " + ex.Message, null,
                                "ERROR", log);
                        }
                        catch (Exception registrationError)
                        {
                            log?.Error("Unable to record isolated batch issue.",
                                registrationError);
                        }
                    }
                    log?.Error("Batch failed for Foundation " + foundation.Id.IntegerValue, ex);
                }
            }

            log?.WriteHeader("BATCH SUMMARY");
            log?.Info(result.ToString());

            return result;
        }

        private static ManholeDetectionResult FromVirtualFootprint(
            Element foundation, VirtualFoundationResult footprint)
        {
            BoundingBoxXYZ box = foundation.get_BoundingBox(null);
            XYZ center = footprint.VirtualCenter;

            var walls = footprint.Walls.Select(w =>
            {
                Line axis = ((LocationCurve)w.Location).Curve as Line;
                XYZ d = new XYZ(axis.Direction.X, axis.Direction.Y, 0).Normalize();
                return new ManholeWall
                {
                    Wall = w, Axis = axis,
                    Direction = d,
                    MidPoint = (axis.GetEndPoint(0) + axis.GetEndPoint(1)) * 0.5,
                    LengthFt = axis.Length
                };
            }).OrderBy(w =>
            {
                XYZ v = w.MidPoint - center;
                double angle = Math.Atan2(v.X, v.Y);
                return angle < 0 ? angle + 2 * Math.PI : angle;
            }).ToList();

            int[] numbering = { 1, 2, 4, 3 };
            for (int i = 0; i < walls.Count; i++) walls[i].Number = numbering[i];

            ManholeWall w1 = walls.First(w => w.Number == 1);
            ManholeWall w2 = walls.First(w => w.Number == 2);
            ManholeWall w3 = walls.First(w => w.Number == 3);
            ManholeWall w4 = walls.First(w => w.Number == 4);

            double span14 = WallSeparation(w1, w4);
            double span23 = WallSeparation(w2, w3);
            double t14 = (w1.Wall.Width + w4.Wall.Width) * 0.5;
            double t23 = (w2.Wall.Width + w3.Wall.Width) * 0.5;

            return new ManholeDetectionResult
            {
                Foundation = foundation,
                FoundationBox = box,
                Center = center,
                FoundationTopZ = box.Max.Z,
                FoundationThicknessFt = box.Max.Z - box.Min.Z,
                ClearW1W4Ft = span14 - t14,
                ClearW2W3Ft = span23 - t23,
                OuterW1W4Ft = span14 + t14,
                OuterW2W3Ft = span23 + t23,
                WallHeightFt = walls.Max(w => w.Wall.get_BoundingBox(null).Max.Z) -
                               walls.Min(w => w.Wall.get_BoundingBox(null).Min.Z),
                CandidateWallIds = footprint.Walls.Select(w => w.Id.IntegerValue).ToList(),
                Walls = walls.OrderBy(w => w.Number).ToList()
            };
        }

        private static double WallSeparation(ManholeWall a, ManholeWall b)
        {
            XYZ tangent = a.Direction;
            XYZ normal = new XYZ(-tangent.Y, tangent.X, 0);
            return Math.Abs((b.MidPoint - a.MidPoint).DotProduct(normal));
        }

        private static void EvaluateLinkAvailability(
            Document doc,
            out int totalLinks,
            out int loadedLinks,
            out int loadedMepLinks)
        {
            List<RevitLinkInstance> links = new FilteredElementCollector(doc)
                .OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>()
                .ToList();

            totalLinks = links.Count;
            loadedLinks = 0;
            loadedMepLinks = 0;

            BuiltInCategory[] categories =
            {
                BuiltInCategory.OST_PipeCurves,
                BuiltInCategory.OST_DuctCurves,
                BuiltInCategory.OST_CableTray,
                BuiltInCategory.OST_Conduit
            };

            foreach (RevitLinkInstance link in links)
            {
                Document linkDoc = link.GetLinkDocument();
                if (linkDoc == null)
                    continue;

                loadedLinks++;

                var filter = new ElementMulticategoryFilter(categories);
                bool hasMep = new FilteredElementCollector(linkDoc)
                    .WherePasses(filter)
                    .WhereElementIsNotElementType()
                    .Take(1)
                    .Any();

                if (hasMep)
                    loadedMepLinks++;
            }
        }

        private static ManholeDataRecord BuildDataRecord(
            Element foundation,
            ManholeDetectionResult manhole,
            string number)
        {
            return new ManholeDataRecord
            {
                ManholeNumber = number,
                FoundationId = foundation.Id.IntegerValue,
                FoundationUniqueId = foundation.UniqueId,
                Wall1Id = manhole.Walls.First(w => w.Number == 1).Wall.Id.IntegerValue,
                Wall2Id = manhole.Walls.First(w => w.Number == 2).Wall.Id.IntegerValue,
                Wall3Id = manhole.Walls.First(w => w.Number == 3).Wall.Id.IntegerValue,
                Wall4Id = manhole.Walls.First(w => w.Number == 4).Wall.Id.IntegerValue,
                CenterXmm = UnitUtil.FtToMm(manhole.Center.X),
                CenterYmm = UnitUtil.FtToMm(manhole.Center.Y),
                BaseTopZmm = UnitUtil.FtToMm(manhole.FoundationTopZ),
                BaseThicknessMm = UnitUtil.FtToMm(manhole.FoundationThicknessFt),
                ClearW1W4Mm = UnitUtil.FtToMm(manhole.ClearW1W4Ft),
                ClearW2W3Mm = UnitUtil.FtToMm(manhole.ClearW2W3Ft),
                OuterW1W4Mm = UnitUtil.FtToMm(manhole.OuterW1W4Ft),
                OuterW2W3Mm = UnitUtil.FtToMm(manhole.OuterW2W3Ft),
                WallHeightMm = UnitUtil.FtToMm(manhole.WallHeightFt)
            };
        }

        private static int ResolveNextNumber(IEnumerable<string> numbers)
        {
            int max = 0;

            foreach (string value in numbers ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(value)) continue;

                string digits = new string(value.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
                int parsed;
                if (int.TryParse(digits, out parsed))
                    max = Math.Max(max, parsed);
            }

            return max + 1;
        }

        private static string NextAvailableNumber(HashSet<string> used, ref int next)
        {
            while (true)
            {
                string candidate = "MH-" + next.ToString("000");
                next++;

                if (!used.Contains(candidate))
                    return candidate;
            }
        }
    }
}
