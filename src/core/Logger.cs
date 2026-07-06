using System;
using System.IO;

namespace SoulmvKit.Core
{
    // Log simples em arquivo (logs/app.log) para depurar a fase de rede na LAN.
    public static class Logger
    {
        private static readonly object _lock = new object();
        private static string _path;

        public static void Init(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                _path = Path.Combine(dir, "app.log");
            }
            catch { }
        }

        public static void Log(string msg)
        {
            try
            {
                lock (_lock)
                {
                    string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg;
                    if (_path != null) File.AppendAllText(_path, line + Environment.NewLine);
                    System.Diagnostics.Debug.WriteLine(line);
                }
            }
            catch { }
        }
    }
}
