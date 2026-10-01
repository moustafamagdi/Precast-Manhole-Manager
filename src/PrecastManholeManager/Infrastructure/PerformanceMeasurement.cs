using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Autodesk.Revit.DB;

namespace Hatco.PrecastManholeManager.Infrastructure
{
    internal static class PerformanceMeasurement
    {
        [ThreadStatic] private static AttributionScope current;
        internal static bool CropBeforeActivation => current?.CropBeforeActivation == true;
        internal sealed class AttributionScope : IDisposable
        {
            internal readonly Document Document;
            internal readonly bool CropBeforeActivation;
            internal readonly bool ExtraRegeneration;
            internal readonly StreamWriter Writer;
            private readonly AttributionScope previous;
            internal AttributionScope(Document doc, DiagnosticLogger log, bool cropBeforeActivation, bool extraRegeneration)
            {
                Document = doc;
                CropBeforeActivation = cropBeforeActivation;
                ExtraRegeneration = extraRegeneration;
                string path = Path.ChangeExtension(log.LogPath, ".performance.csv");
                bool header = !File.Exists(path);
                Writer = new StreamWriter(path, true) { AutoFlush = true };
                if (header) Writer.WriteLine("Operation,Item,Seconds,Returned");
                previous = current;
                current = this;
                log.Info("ATTRIBUTION CSV: " + path);
                log.Info("EXTRA DIAGNOSTIC REGENERATION: " + extraRegeneration);
                log.Info("CROP ORDER EXPERIMENT: " + (cropBeforeActivation ? "SET_BOUNDS_THEN_ACTIVATE" : "BASELINE_ACTIVATE_THEN_SET_BOUNDS"));
            }
            public void Dispose() { current = previous; Writer.Dispose(); }
        }
        internal static IDisposable BeginAttribution(Document doc, DiagnosticLogger log, bool cropBeforeActivation = false, bool extraRegeneration = true) => new AttributionScope(doc, log, cropBeforeActivation, extraRegeneration);
        private static string Csv(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
        internal static bool CanAttribute(string operation, bool modifiable) => modifiable &&
            !operation.StartsWith("Regenerate") && !operation.StartsWith("Transaction.") &&
            !operation.StartsWith("Document.Save");
        // Each record describes one API call, not a sum of nested stage timers.
        internal static T Call<T>(DiagnosticLogger log, string operation, string item, Func<T> action)
        {
            bool attribute = current != null && current.ExtraRegeneration && CanAttribute(operation, current.Document.IsModifiable);
            if (attribute) Raw(log, operation + ".PreRegen", item, () => { current.Document.Regenerate(); return true; });
            T result = Raw(log, operation, item, action);
            if (attribute) Raw(log, operation + ".PostRegen", item, () => { current.Document.Regenerate(); return true; });
            return result;
        }
        private static T Raw<T>(DiagnosticLogger log, string operation, string item, Func<T> action)
        {
            bool succeeded = false;
            var watch = Stopwatch.StartNew();
            try
            {
                T result = action();
                succeeded = true;
                return result;
            }
            finally
            {
                watch.Stop();
                current?.Writer.WriteLine(Csv(operation) + "," + Csv(item) + "," +
                    watch.Elapsed.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture) + "," + succeeded);
                log.Info("PERF_CALL Operation=" + operation + " Item=" + item +
                    " Seconds=" + watch.Elapsed.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture) +
                    " Returned=" + succeeded);
            }
        }

        internal static void Call(DiagnosticLogger log, string operation, string item, Action action)
        {
            Call(log, operation, item, () => { action(); return true; });
        }
    }
}
