using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Hatco.PrecastManholeManager.Infrastructure;
using Hatco.PrecastManholeManager.Models;

namespace Hatco.PrecastManholeManager.Services
{
    internal sealed class CleanSyncApplyOptions
    {
        public bool ResetEditedProfiles { get; set; }
        public bool RemoveManualNative { get; set; }
        public bool RemoveVoidCutRelations { get; set; }
        public bool DeleteIsolatedInPlaceCutters { get; set; }
        public bool IncludeStraightVirtual { get; set; }
        public bool RequiredLinksVerified { get; set; }
        public bool AllowIncompleteLinkCoverageForPreview { get; set; }
    }

    internal sealed class CleanSyncApplyResult
    {
        public bool Committed { get; set; }
        public string Error { get; set; }
        public int ProfilesReset { get; set; }
        public int UnattachedVoidRelationsRemoved { get; set; }
        public int ManualNativeDeleted { get; set; }
        public int InPlaceCuttersUnpinnedDeleted { get; set; }
        public int ManagedUnchanged { get; set; }
        public int ManagedUpdated { get; set; }
        public int NewOpenings { get; set; }
        public int VirtualDeferred { get; set; }
        public int ManagedStalePreserved { get; set; }
        public override string ToString()
        {
            return (Committed ? "COMMITTED" : "NOT COMMITTED / ROLLED BACK") +
                "\nEdited profiles reset: " + ProfilesReset +
                "\nVoid cut relations removed: " + UnattachedVoidRelationsRemoved +
                "\nNon-tool native openings removed: " + ManualNativeDeleted +
                "\nPinned/unpinned isolated in-place cutters deleted: " +
                InPlaceCuttersUnpinnedDeleted +
                "\nManaged unchanged: " + ManagedUnchanged +
                "\nManaged updated: " + ManagedUpdated +
                "\nNew openings created: " + NewOpenings +
                "\nVirtual/sloped deferred: " + VirtualDeferred +
                "\nStale managed preserved: " + ManagedStalePreserved +
                (string.IsNullOrWhiteSpace(Error) ? "" : "\nERROR: " + Error);
        }
    }

    // Single selected manhole ONLY; one atomic transaction. On ANY error,
    // rollback restores all old profiles, cuts and openings of this manhole.
    internal static class CleanSyncAtomicService
    {
        public static CleanSyncApplyResult Apply(Document doc, CleanSyncPlan plan,
            CleanSyncApplyOptions options, DiagnosticLogger log)
        {
            var output = new CleanSyncApplyResult();
            if (doc == null || plan == null || options == null)
                throw new ArgumentNullException("Valid cleanup plan and options are required.");
            if (doc.IsReadOnly || doc.IsLinked)
                throw new InvalidOperationException("Document cannot be modified.");
            if (!string.IsNullOrWhiteSpace(plan.BlockReason))
                throw new InvalidOperationException("Cleanup blocked: " + plan.BlockReason);
            if (plan.UnsupportedSolidCutWallIds.Count != 0)
                throw new InvalidOperationException("Unclassified solid cuts exist.");
            if (plan.UnavailableLinks > 0 && !options.RequiredLinksVerified &&
                !options.AllowIncompleteLinkCoverageForPreview)
                throw new InvalidOperationException(
                    "Required link coverage was not acknowledged: " +
                    plan.UnavailableLinks + " Revit links unavailable.");
            if (plan.ProfileResetCount > 0 && !options.ResetEditedProfiles)
                throw new InvalidOperationException("Edited profiles require explicit reset approval.");
            if (plan.VoidCutCount > 0 && !options.RemoveVoidCutRelations)
                throw new InvalidOperationException("Void-cut relations require explicit uncut approval.");
            if (plan.ManualOpeningIds.Count > 0 && !options.RemoveManualNative)
                throw new InvalidOperationException("Manual native openings require explicit delete approval.");
            if (plan.InPlaceCutterCount > 0 &&
                !options.DeleteIsolatedInPlaceCutters)
                throw new InvalidOperationException(
                    "In-place cutter deletion requires separate explicit approval.");

            var selected = new List<PenetrationRecord>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (UnifiedOpeningReviewRow row in plan.ProposedRows)
            {
                if (row.IsVirtual &&
                    (!options.IncludeStraightVirtual ||
                     Math.Abs(row.SlopePercent) > 0.1 ||
                     row.ApproachAngleDeg > 0.5 ||
                     row.Detection == "AMBIGUOUS"))
                {
                    output.VirtualDeferred++;
                    log.Warn("VIRTUAL DEFERRED Source=" + row.SourceId +
                        " Wall=" + row.WallId +
                        " Sloped/skewed/disabled virtual case.");
                    continue;
                }

                PenetrationRecord source = row.Source;
                if (source == null || source.CutWidthMm <= 0 ||
                    source.CutHeightMm <= 0 ||
                    string.IsNullOrWhiteSpace(source.SourceKey))
                    throw new InvalidOperationException(
                        "Unresolved opening or source key: " + row.SourceId);
                if (!keys.Add(source.SourceKey))
                    throw new InvalidOperationException(
                        "Ambiguous duplicate source/wall: " + source.SourceKey);
                if (!plan.WallIds.Contains(source.HostWallId))
                    throw new InvalidOperationException(
                        "Opening targets a wall outside the selected manhole.");
                selected.Add(source);
            }

            log.WriteHeader("ATOMIC CLEAN AND SYNC - SINGLE MANHOLE");
            if (plan.UnavailableLinks > 0 &&
                options.AllowIncompleteLinkCoverageForPreview)
                log.Warn("PARTIAL LINK TEST: " + plan.UnavailableLinks +
                    " links unavailable. Created openings will be " +
                    "PRELIMINARY - NOT FOR ISSUE.");
            log.Warn("Only perform this command on a disposable RVT test copy.");
            log.Info("Foundation=" + plan.FoundationId +
                " Desired=" + selected.Count +
                " VirtualDeferred=" + output.VirtualDeferred);

            using (var tx = new Transaction(doc,
                "HATCO - Test Clean and Synchronize Manhole " + plan.FoundationId))
            {
                var failure = new OpeningFailurePreprocessor(log);
                var failureOptions = tx.GetFailureHandlingOptions();
                failureOptions.SetFailuresPreprocessor(failure);
                failureOptions.SetClearAfterRollback(true);
                tx.SetFailureHandlingOptions(failureOptions);
                tx.Start();

                try
                {
                    foreach (int wallId in plan.WallIds)
                    {
                        Wall wall = doc.GetElement(new ElementId(wallId)) as Wall;
                        if (wall == null)
                            throw new InvalidOperationException(
                                "Target wall disappeared: " + wallId);

                        string profile = plan.Profiles[wallId];
                        if (profile == "EDITED PROFILE")
                        {
                            if (wall.SketchId == ElementId.InvalidElementId)
                                throw new InvalidOperationException(
                                    "Profile changed since the preview: " + wallId);
                            wall.RemoveProfileSketch();
                            output.ProfilesReset++;
                            log.Warn("RESET PROFILE Wall=" + wallId);
                        }
                        else if (profile != "NO EDITED SKETCH" ||
                                 wall.SketchId != ElementId.InvalidElementId)
                            throw new InvalidOperationException(
                                "Wall profile status changed since preview: " + wallId);

                        List<int> expectedVoidIds = plan.VoidCutIds[wallId];
                        List<int> actualVoidIds =
                            InstanceVoidCutUtils.GetCuttingVoidInstances(wall)
                                .Select(x => x.IntegerValue).OrderBy(x => x).ToList();
                        if (!expectedVoidIds.OrderBy(x => x)
                            .SequenceEqual(actualVoidIds))
                            throw new InvalidOperationException(
                                "Void cutting relationships changed since preview on wall " +
                                wallId);

                        foreach (int cutterId in expectedVoidIds)
                        {
                            Element cutting = doc.GetElement(new ElementId(cutterId));
                            if (cutting == null)
                                throw new InvalidOperationException(
                                    "Cutting instance missing: " + cutterId);
                            // Only the relationship with this wall is removed:
                            // the in-place family itself is NEVER deleted.
                            InstanceVoidCutUtils.RemoveInstanceVoidCut(
                                doc, wall, cutting);
                            output.UnattachedVoidRelationsRemoved++;
                            log.Warn("REMOVE VOID CUT RELATION Wall=" + wallId +
                                     " Cutter=" + cutterId);
                        }
                    }

                    // This route is EXPLICIT test-copy deletion of a pinned
                    // in-place cutter instance, not the family/type. The read-only
                    // plan has verified no OTHER WALL advertises this insert.
                    // Non-wall cuts cannot be exhaustively inferred from
                    // FindInserts, so the UI requires additional acknowledgement.
                    foreach (var pair in plan.InPlaceCutterWallIds)
                    {
                        FamilyInstance instance = doc.GetElement(
                            new ElementId(pair.Key)) as FamilyInstance;
                        if (instance?.Symbol?.Family == null ||
                            !instance.Symbol.Family.IsInPlace)
                            throw new InvalidOperationException(
                                "In-place cutter identity changed: " + pair.Key);

                        foreach (int hostId in pair.Value)
                        {
                            Wall host = doc.GetElement(new ElementId(hostId)) as Wall;
                            if (host == null || !host.FindInserts(true, true, true, true)
                                .Any(id => id.IntegerValue == pair.Key))
                                throw new InvalidOperationException(
                                    "Cutter/host relation changed: Cutter=" +
                                    pair.Key + " Wall=" + hostId);
                        }

                        // A dry-run SubTransaction catches cascade deletion
                        // before committing real deletion; it cannot detect
                        // changed geometry in otherwise surviving elements.
                        using (var trial = new SubTransaction(doc))
                        {
                            trial.Start();
                            ICollection<ElementId> nested =
                                instance.GetSubComponentIds();
                            if (instance.Pinned) instance.Pinned = false;
                            ICollection<ElementId> removed = doc.Delete(
                                instance.Id);
                            var permitted = new HashSet<int> {
                                pair.Key
                            };
                            // Family instance subcomponents may be deleted
                            // with the instance, but other project elements
                            // must not disappear.
                            foreach (ElementId id in nested)
                                permitted.Add(id.IntegerValue);
                            List<int> extraIds = removed
                                .Select(x => x.IntegerValue)
                                .Where(id => !permitted.Contains(id))
                                .Distinct().OrderBy(id => id).ToList();
                            // IMPORTANT: Inspect only AFTER rolling back the
                            // trial deletion, since deleted elements cannot be
                            // reliably retrieved while the trial is open.
                            trial.RollBack();
                            if (extraIds.Count > 0)
                            {
                                log.Warn("INPLACE DELETE DRY-RUN Cutter=" +
                                    pair.Key + " WouldDeleteIds=" +
                                    string.Join(",", removed.Select(x => x.IntegerValue)) +
                                    " NestedIds=" + string.Join(",", permitted) +
                                    " UnexpectedIds=" + string.Join(",", extraIds));
                                foreach (int extraId in extraIds)
                                {
                                    Element affected = doc.GetElement(
                                        new ElementId(extraId));
                                    FamilyInstance affectedInstance =
                                        affected as FamilyInstance;
                                    log.Warn("INPLACE CASCADE DETAIL Cutter=" +
                                        pair.Key + " Affected=" + extraId +
                                        " Class=" + (affected?.GetType().FullName ?? "NOT_FOUND_AFTER_ROLLBACK") +
                                        " Category=" + (affected?.Category?.Name ?? "<none>") +
                                        " Name=" + (affected?.Name ?? "<none>") +
                                        " Pinned=" +
                                            (affected == null ? "<unknown>" :
                                                affected.Pinned.ToString()) +
                                        " Family=" +
                                            (affectedInstance?.Symbol?.Family?.Name ?? "<none>"));
                                }
                                throw new InvalidOperationException(
                                    "In-place cutter deletion would remove " +
                                    extraIds.Count + " additional elements. " +
                                    "Cutter=" + pair.Key +
                                    " ExtraIds=" + string.Join(",", extraIds) +
                                    ". No model changes were committed. " +
                                    "Inspect INPLACE CASCADE DETAIL in log.");
                            }
                        }

                        // Re-fetch after trial rollback: old managed wrappers
                        // should never be relied upon following regeneration.
                        instance = doc.GetElement(new ElementId(pair.Key))
                            as FamilyInstance;
                        if (instance == null)
                            throw new InvalidOperationException(
                                "Cutter unavailable after deletion dry-run: " +
                                pair.Key);
                        if (instance.Pinned)
                        {
                            instance.Pinned = false;
                            log.Warn("UNPIN INPLACE CUTTER " + pair.Key);
                        }
                        ICollection<ElementId> deleted = doc.Delete(instance.Id);
                        if (deleted.Any(id => plan.WallIds.Contains(
                                id.IntegerValue) ||
                                plan.ManagedOpeningIds.Values.Contains(
                                    id.IntegerValue)))
                            throw new InvalidOperationException(
                                "In-place cut deletion affected protected wall/managed opening.");
                        output.InPlaceCuttersUnpinnedDeleted++;
                        log.Warn("DELETE INPLACE CUTTER INSTANCE " + pair.Key +
                            " WALLS=" + string.Join(",", pair.Value));
                    }

                    // Confirm Revit regeneration succeeds BEFORE removing any
                    // native openings. Revit can occasionally delete dependent
                    // elements when a profile reset occurs.
                    doc.Regenerate();
                    foreach (var item in plan.ManagedOpeningIds)
                        if (!(doc.GetElement(new ElementId(item.Value)) is Opening))
                            throw new InvalidOperationException(
                                "Profile/void reset affected previously managed opening " +
                                item.Value + ". All changes will roll back.");

                    HashSet<int> managedIds = new HashSet<int>(
                        plan.ManagedOpeningIds.Values);
                    foreach (int openingId in plan.ManualOpeningIds)
                    {
                        Opening opening = doc.GetElement(
                            new ElementId(openingId)) as Opening;
                        if (opening == null)
                            throw new InvalidOperationException(
                                "Manual opening changed since preview: " + openingId);
                        ManagedOpeningData unexpected;
                        if (OpeningStorageService.TryRead(opening, out unexpected) &&
                            (!unexpected.AdoptedManual ||
                             !plan.AdoptedManualOpeningIds.Contains(openingId)))
                            throw new InvalidOperationException(
                                "Manual opening became tool-managed or changed: " +
                                openingId);

                        ICollection<ElementId> deleted = doc.Delete(opening.Id);
                        if (deleted.Any(id => plan.WallIds.Contains(id.IntegerValue) ||
                                               managedIds.Contains(id.IntegerValue)))
                            throw new InvalidOperationException(
                                "Deleting manual opening would remove a wall or managed opening.");
                        output.ManualNativeDeleted++;
                        log.Warn("DELETE NON-TOOL NATIVE OPENING " + openingId);
                    }
                    doc.Regenerate();

                    foreach (PenetrationRecord record in selected)
                    {
                        ManagedOpeningData oldData = null;
                        Opening old = null;
                        int oldId;
                        if (plan.ManagedOpeningIds.TryGetValue(
                            record.SourceKey, out oldId))
                        {
                            old = doc.GetElement(new ElementId(oldId)) as Opening;
                            if (old == null || !OpeningStorageService.TryRead(old, out oldData) ||
                                oldData.SourceKey != record.SourceKey)
                                throw new InvalidOperationException(
                                    "Managed opening disappeared or changed: " + oldId);

                            if (Matches(oldData, record))
                            {
                                output.ManagedUnchanged++;
                                log.Info("UNCHANGED MANAGED Opening=" + oldId +
                                         " Key=" + record.SourceKey);
                                continue;
                            }
                        }

                        string reason;
                        if (!OpeningFitValidationService.TryValidate(
                            doc, record, out reason))
                            throw new InvalidOperationException(
                                "Unsafe opening fit for source " + record.LinkedElementId +
                                " Wall=" + record.HostWallId + ": " + reason);

                        if (old != null)
                        {
                            doc.Delete(old.Id);
                            output.ManagedUpdated++;
                        }
                        else output.NewOpenings++;

                        Opening opening = NewRectangularOpening(doc, record);
                        OpeningStorageService.Write(opening, record);
                        log.Info((old != null ? "UPDATE" : "CREATE") +
                            " MANAGED Opening=" + opening.Id.IntegerValue +
                            " Key=" + record.SourceKey +
                            " Size=" + record.CutWidthMm.ToString("0.#") +
                            "x" + record.CutHeightMm.ToString("0.#") + "mm");
                        doc.Regenerate();
                    }

                    // Unmatched managed openings stay in place: unloaded links,
                    // outdated source identity and incomplete scans must never
                    // trigger accidental deletion.
                    output.ManagedStalePreserved = plan.ManagedOpeningIds.Keys
                        .Count(x => !keys.Contains(x));
                    foreach (var item in plan.ManagedOpeningIds)
                        if (!keys.Contains(item.Key))
                            log.Warn("STALE MANAGED PRESERVED Opening=" + item.Value +
                                     " Key=" + item.Key);

                    TransactionStatus status = tx.Commit();
                    if (status != TransactionStatus.Committed)
                        throw new InvalidOperationException(
                            "Revit failed to commit safe clean/sync. " +
                            "Rollback triggered by failure preprocessor.");

                    output.Committed = true;
                    log.Info("ATOMIC CLEAN SYNC COMMITTED: " + output);
                }
                catch (Exception ex)
                {
                    if (tx.GetStatus() == TransactionStatus.Started)
                        tx.RollBack();
                    output.Error = ex.Message;
                    output.Committed = false;
                    output.ProfilesReset = 0;
                    output.UnattachedVoidRelationsRemoved = 0;
                    output.ManualNativeDeleted = 0;
                    output.InPlaceCuttersUnpinnedDeleted = 0;
                    output.ManagedUpdated = 0;
                    output.NewOpenings = 0;
                    log.Error("ATOMIC CLEAN SYNC ABORTED; changes rolled back.", ex);
                }
            }
            return output;
        }

        private static bool Matches(ManagedOpeningData data, PenetrationRecord r)
        {
            const double tol = 1.0;
            return data.HostWallId == r.HostWallId &&
                Math.Abs(data.Xmm - r.Xmm) <= tol &&
                Math.Abs(data.Ymm - r.Ymm) <= tol &&
                Math.Abs(data.Zmm - r.Zmm) <= tol &&
                Math.Abs(data.CutWidthMm - r.CutWidthMm) <= tol &&
                Math.Abs(data.CutHeightMm - r.CutHeightMm) <= tol;
        }

        private static Opening NewRectangularOpening(Document doc,
            PenetrationRecord r)
        {
            Wall wall = doc.GetElement(new ElementId(r.HostWallId)) as Wall;
            Line axis = (wall?.Location as LocationCurve)?.Curve as Line;
            if (axis == null)
                throw new InvalidOperationException("Host wall is not straight.");
            XYZ t = new XYZ(axis.Direction.X, axis.Direction.Y, 0);
            if (t.GetLength() < 1e-9)
                throw new InvalidOperationException("Invalid host wall axis.");
            t = t.Normalize();
            XYZ center = new XYZ(UnitUtil.MmToFt(r.EffectiveOpeningXmm),
                UnitUtil.MmToFt(r.EffectiveOpeningYmm),
                UnitUtil.MmToFt(r.EffectiveOpeningZmm));
            center = OpeningHostPlaneService.MoveToWallSolidMidPlane(
                wall, center);
            double hw = UnitUtil.MmToFt(r.CutWidthMm) / 2;
            double hh = UnitUtil.MmToFt(r.CutHeightMm) / 2;
            XYZ ll = center - t * hw - XYZ.BasisZ * hh;
            XYZ ur = center + t * hw + XYZ.BasisZ * hh;
            // No automatic UnjoinGeometry in destructive cleanup tests:
            // any join failure rolls back the complete manhole.
            return doc.Create.NewOpening(wall, ll, ur);
        }
    }
}
