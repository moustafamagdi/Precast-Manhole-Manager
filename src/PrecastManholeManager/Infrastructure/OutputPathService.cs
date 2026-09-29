using System;
using System.IO;

namespace Hatco.PrecastManholeManager.Infrastructure
{
    internal static class OutputPathService
    {
        private const string RootFolderName = "Precast Manhole Manager";
        private const string LogsFolderName = "Logs";

        public static string GetLogsFolder()
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string root = Path.Combine(desktop, RootFolderName);
            string logs = Path.Combine(root, LogsFolderName);

            Directory.CreateDirectory(logs);
            return logs;
        }
    }
}
