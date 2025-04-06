using System.Diagnostics;
using System.IO;
using System;

public static class Logger
{
    public enum LogType { INFO, WARN, ERROR, DEBUG }
    public static bool ShowInfo = true;
    public static bool ShowDebug = true;
    private static StreamWriter _logWriter;
    private static readonly object _lock = new object();
    private static string _logFp = "";

    public static void Init()
    {
        string tempDir = Path.GetTempPath();
        string logFileName = $"fs-png_{DateTime.Now.ToString("yyyyMMdd_HHmmss")}.log";
        string logFilePath = Path.Combine(tempDir, logFileName);
        _logFp = logFilePath;
        _logWriter = new StreamWriter(new FileStream(logFilePath, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true
        };
    }
    public static void OpenLogFile()
    {
        var psi = new ProcessStartInfo
        {
            FileName = _logFp,
            UseShellExecute = true
        };
        Process.Start(psi);
    }

    public static void Log(LogType type, string msg)
    {
        if (!ShowInfo)
            return;
        if (!ShowDebug && type == LogType.DEBUG)
            return;
        string logMessage = $"** [{type}] {DateTime.Now.ToUniversalTime():R} : {msg}";
        lock (_lock)
        {
            if (_logWriter != null)
            {
                _logWriter.WriteLine(logMessage);
            }
            else
            {
                throw new InvalidOperationException("Logger is not initialized");
            }
        }
    }
    public static void CleanUp()
    {
        try
        {
            _logWriter.Close();
            _logWriter.Dispose();
        }
        catch { }
        try
        {
            File.Delete(_logFp);
        }
        catch { }
    }
}