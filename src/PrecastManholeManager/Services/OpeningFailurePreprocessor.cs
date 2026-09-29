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

        public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
        {
            IList<FailureMessageAccessor> failures = failuresAccessor.GetFailureMessages();
            bool resolvedAny = false;
            bool unresolvedTarget = false;

            foreach (FailureMessageAccessor failure in failures)
            {
                string description = failure.GetDescriptionText() ?? string.Empty;
                if (!IsTargetFailure(description))
                    continue;

                FailureSeverity severity = failure.GetSeverity();

                _log?.Warn(
                    "REVIT FAILURE intercepted: Severity=" + severity +
                    " Description='" + description + "'");

                if (severity == FailureSeverity.Warning)
                {
                    failuresAccessor.DeleteWarning(failure);
                    ResolvedCount++;
                    resolvedAny = true;
                    continue;
                }

                FailureResolutionType resolution;
                if (TryChooseResolution(failure, description, out resolution))
                {
                    try
                    {
                        failure.SetCurrentResolutionType(resolution);
                        failuresAccessor.ResolveFailure(failure);

                        ResolvedCount++;
                        resolvedAny = true;

                        _log?.Warn(
                            "REVIT FAILURE auto-resolved using " + resolution +
                            ": '" + description + "'");
                        continue;
                    }
                    catch (Exception ex)
                    {
                        _log?.Warn(
                            "REVIT FAILURE resolution attempt failed: " + ex.Message);
                    }
                }

                UnresolvedTargetCount++;
                unresolvedTarget = true;

                _log?.Warn(
                    "REVIT FAILURE could not be auto-resolved; current manhole transaction will roll back: '" +
                    description + "'");
            }

            if (unresolvedTarget)
                return FailureProcessingResult.ProceedWithRollBack;

            if (resolvedAny)
                return FailureProcessingResult.ProceedWithCommit;

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

        private static bool TryChooseResolution(
            FailureMessageAccessor failure,
            string description,
            out FailureResolutionType resolution)
        {
            if (description.IndexOf(
                    "Can't keep elements joined",
                    StringComparison.OrdinalIgnoreCase) >= 0 &&
                failure.HasResolutionOfType(FailureResolutionType.DetachElements))
            {
                resolution = FailureResolutionType.DetachElements;
                return true;
            }

            if (failure.HasResolutionOfType(FailureResolutionType.SkipElements))
            {
                resolution = FailureResolutionType.SkipElements;
                return true;
            }

            if (failure.HasResolutionOfType(FailureResolutionType.DeleteElements))
            {
                resolution = FailureResolutionType.DeleteElements;
                return true;
            }

            if (failure.HasResolutionOfType(FailureResolutionType.DetachElements))
            {
                resolution = FailureResolutionType.DetachElements;
                return true;
            }

            resolution = FailureResolutionType.Invalid;
            return false;
        }
    }
}
