using System;
using System.Diagnostics;
using System.Globalization;

namespace Hatco.PrecastManholeManager.Infrastructure
{
    internal static class PerformanceMeasurement
    {
        // Each record describes one API call, not a sum of nested stage timers.
        internal static T Call<T>(DiagnosticLogger log, string operation, string item, Func<T> action)
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
