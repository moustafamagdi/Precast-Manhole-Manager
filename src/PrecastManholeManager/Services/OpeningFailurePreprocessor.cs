using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Hatco.PrecastManholeManager.Infrastructure;

namespace Hatco.PrecastManholeManager.Services
{
    internal sealed class OpeningFailurePreprocessor : IFailuresPreprocessor
    {
        private readonly DiagnosticLogger _log;
        private readonly HashSet<int> _allowedJoinIds;
        private readonly HashSet<int> _wallIds;
        private readonly HashSet<string> _attempted = new HashSet<string>();

        public OpeningFailurePreprocessor(DiagnosticLogger log,
            IEnumerable<int> allowedJoinIds = null, IEnumerable<int> wallIds = null)
        {
            _log = log;
            _allowedJoinIds = new HashSet<int>(allowedJoinIds ?? Enumerable.Empty<int>());
            _wallIds = new HashSet<int>(wallIds ?? Enumerable.Empty<int>());
        }

        public int ResolvedCount { get; private set; }
        public int UnresolvedTargetCount { get; private set; }

        public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
        {
            bool resolved = false;
            foreach (FailureMessageAccessor failure in accessor.GetFailureMessages())
            {
                string description = failure.GetDescriptionText() ?? string.Empty;
                FailureDefinitionId definition = failure.GetFailureDefinitionId();
                bool joinFailure = IsJoinFailure(definition);
                var failing = failure.GetFailingElementIds().Select(x => x.IntegerValue).ToList();
                var additional = failure.GetAdditionalElementIds().Select(x => x.IntegerValue).ToList();
                var affected = failing.Concat(additional).Distinct().OrderBy(x => x).ToList();
                string details = " Definition=" + definition.Guid +
                    " Severity=" + failure.GetSeverity() +
                    " FailingIds=" + string.Join(",", failing) +
                    " AdditionalIds=" + string.Join(",", additional) +
                    " Description='" + description + "'";
                _log?.Warn("REVIT FAILURE:" + details);

                if (CanDetachJoin(joinFailure, affected, _allowedJoinIds, _wallIds) &&
                    failure.HasResolutionOfType(FailureResolutionType.DetachElements) &&
                    accessor.IsFailureResolutionPermitted(failure, FailureResolutionType.DetachElements))
                {
                    string key = definition.Guid + "|" + string.Join(",", affected);
                    if (_attempted.Add(key))
                    {
                        try
                        {
                            // Resolve only this reported relationship, never delete elements.
                            failure.SetCurrentResolutionType(FailureResolutionType.DetachElements);
                            accessor.ResolveFailure(failure);
                            ResolvedCount++;
                            resolved = true;
                            _log?.Warn("RESOLVE MANHOLE JOIN: DetachElements" + details);
                            continue;
                        }
                        catch (Exception ex)
                        {
                            _log?.Warn("MANHOLE JOIN RESOLUTION FAILED: " + ex.Message);
                        }
                    }
                }

                // Unknown errors and non-cutting openings remain fatal. A repeated
                // join failure is rolled back rather than retried indefinitely.
                if (joinFailure || definition == BuiltInFailures.OpeningFailures.OpeningRectDoesnotCutHost ||
                    definition == BuiltInFailures.OpeningFailures.OpeningDoesnotCutHost ||
                    definition == BuiltInFailures.OpeningFailures.OpeningDoesnotCutHostDelete ||
                    IsTargetFailure(description) ||
                    failure.GetSeverity() == FailureSeverity.Error ||
                    failure.GetSeverity() == FailureSeverity.DocumentCorruption)
                {
                    UnresolvedTargetCount++;
                    _log?.Warn("REVIT OPENING FAILURE: ROLLBACK REQUIRED." + details);
                    return FailureProcessingResult.ProceedWithRollBack;
                }
            }
            // Revit regenerates and checks the model again after the resolution.
            return resolved ? FailureProcessingResult.ProceedWithCommit :
                FailureProcessingResult.Continue;
        }

        internal static bool CanDetachJoin(bool knownJoinFailure, IEnumerable<int> affected,
            IEnumerable<int> allowed, IEnumerable<int> walls)
        {
            var ids = new HashSet<int>(affected);
            var scope = new HashSet<int>(allowed);
            return knownJoinFailure && ids.Count >= 2 && ids.IsSubsetOf(scope) &&
                ids.Overlaps(walls);
        }

        private static bool IsJoinFailure(FailureDefinitionId id)
        {
            return id == BuiltInFailures.JoinElementsFailures.CannotKeepJoined ||
                id == BuiltInFailures.JoinElementsFailures.CannotJoinElementsError ||
                id == BuiltInFailures.JoinElementsFailures.CannotJoinElementsWarn ||
                id == BuiltInFailures.JoinElementsFailures.CannotJoinElementsStructuralError ||
                id == BuiltInFailures.JoinElementsFailures.CannotJoinElementsStructural ||
                id == BuiltInFailures.JoinElementsFailures.CannotJoinElementsMultiPlaneError ||
                id == BuiltInFailures.JoinElementsFailures.CannotJoinElements;
        }

        private static bool IsTargetFailure(string description)
        {
            return description.IndexOf("Rectangular opening doesn't cut its host",
                       StringComparison.OrdinalIgnoreCase) >= 0 ||
                   description.IndexOf("Can't keep elements joined",
                       StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
