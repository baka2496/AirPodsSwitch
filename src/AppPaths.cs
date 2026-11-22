using System;
using System.IO;

namespace AirPodsSwitch
{
    internal static class AppPaths
    {
        internal static readonly string DataDirectory = Initialize();
        internal static string NamesFile { get { return Path.Combine(DataDirectory, "names.txt"); } }

        private static string Initialize()
        {
            string directory = Environment.GetEnvironmentVariable("AIRPODSSWITCH_DATA_DIR");
            if (string.IsNullOrWhiteSpace(directory))
                directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AirPodsSwitch");
            return Directory.CreateDirectory(Path.GetFullPath(directory)).FullName;
        }
    }

    internal static class LogStore
    {
        private static readonly object Sync = new object();
        internal static void AppendAndTrim(string path, string line, int maxLines)
        {
            lock (Sync)
            {
                try
                {
                    File.AppendAllText(path, line + Environment.NewLine, System.Text.Encoding.UTF8);
                    TrimIfNeeded(path, maxLines);
                }
                catch { }
            }
        }
        internal static void TrimIfNeeded(string path, int maxLines)
        {
            if (maxLines <= 0 || !File.Exists(path)) return;
            string[] lines = File.ReadAllLines(path, System.Text.Encoding.UTF8);
            if (lines.Length <= maxLines + 200) return;
            File.WriteAllLines(path, TrimLines(lines, maxLines), System.Text.Encoding.UTF8);
        }
        internal static string[] TrimLines(string[] lines, int maxLines)
        {
            if (lines == null || lines.Length <= maxLines) return lines ?? new string[0];
            string[] kept = new string[maxLines];
            Array.Copy(lines, lines.Length - maxLines, kept, 0, maxLines);
            return kept;
        }
    }

    internal static class Program
    {
        internal static Guid EventContext { get { return new Guid("561006AE-ADB9-4E2A-B401-0666365BAD59"); } }
        internal static void Log(string message)
        {
            LogStore.AppendAndTrim(Path.Combine(AppPaths.DataDirectory, "activity.log"), DateTimeOffset.Now.ToString("o") + " " + message, 1000);
        }
    }
}
