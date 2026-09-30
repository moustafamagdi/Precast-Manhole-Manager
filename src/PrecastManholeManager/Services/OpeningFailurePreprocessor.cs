using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Hatco.PrecastManholeManager.Infrastructure;

namespace Hatco.PrecastManholeManager.Services
{
    internal sealed class OpeningFailurePreprocessor : IFailuresPreprocessor
    {
        private readonly DiagnosticLogger _log;

        public OpeningFailurePreprocessor(DiagnosticLogger log)
        {
            _log = log;
        }

        public int ResolvedCount { get; private set; }
        public int UnresolvedTargetCount { get; private set; }

        public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
        {
            // Never dismiss "doesn't cut host" warnings: doing so could commit
            // misleading, non-cutting openings. Never unjoin unknown elements
            // automatically in the experimental branch.
            foreach (FailureMessageAccessor failure in accessor.GetFailureMessages())
            {
                string description = failure.GetDescriptionText() ?? string.Empty;
                if (!IsTargetFailure(description))
                    continue;

                UnresolvedTargetCount++;
                _log?.Warn("REVIT OPENING FAILURE: ROLLBACK REQUIRED. " +
                           "Severity=" + failure.GetSeverity() +
                           " Description='" + description + "'");
                return FailureProcessingResult.ProceedWithRollBack;
            }

            return FailureProcessingResult.Continue;
        }

        private static bool IsTargetFailure(string description)
        {
            return description.IndexOf(
                       "Rectangular opening doesn't cut its host",
                       StringComparison.OrdinalIgnoreCase) >= 0 ||
                   description.IndexOf(
                       "Can't keep elements joined",
                       StringComparison.OrdinalIgnoreCase) >= 0;
        }

    }
}
