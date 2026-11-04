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

    internal static class Program
    {
        internal static Guid EventContext { get { return new Guid("561006AE-ADB9-4E2A-B401-0666365BAD59"); } }
        internal static void Log(string message)
        {
            try { File.AppendAllText(Path.Combine(AppPaths.DataDirectory, "activity.log"), DateTimeOffset.Now.ToString("o") + " " + message + Environment.NewLine); }
            catch { }
        }
    }
}
