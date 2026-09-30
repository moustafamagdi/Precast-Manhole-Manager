using Autodesk.Revit.DB;
using Hatco.PrecastManholeManager.Services;

namespace Hatco.PrecastManholeManager.Infrastructure
{
    internal static class TransactionFailureHandling
    {
        internal static void Configure(Transaction tx, DiagnosticLogger log)
        {
            var options = tx.GetFailureHandlingOptions();
            options.SetFailuresPreprocessor(new OpeningFailurePreprocessor(log));
            options.SetClearAfterRollback(true); tx.SetFailureHandlingOptions(options);
        }

    }
}
