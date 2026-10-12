using System;
using System.IO;
using System.Text;

namespace RynthCore.Plugin.UbRythai;

/// <summary>
/// Built-in rolling file logger for ub-Rythai.
/// Writes to the local install folder under "Logs" and rolls by size.
/// </summary>
internal sealed class UbRythaiFileLogger
{
    private const long MaxBytesPerFile = 5 * 1024 * 1024; // 5 MB
    private const int MaxRolledFiles = 10;

    private readonly object _sync = new();
    private readonly string _logsDir;
    private readonly string _activeFilePath;

    public UbRythaiFileLogger(string installBaseDir)
    {
        // Keep logs colocated with the local runtime install for easy support collection.
        _logsDir = Path.Combine(installBaseDir, "Logs");
        _activeFilePath = Path.Combine(_logsDir, "ub-Rythai.log");
    }

    /// <summary>
    /// Writes one log line if <paramref name="level"/> is enabled by <paramref name="minimumLevel"/>.
    /// </summary>
    public void Write(UbRythaiLogLevel level, UbRythaiLogLevel minimumLevel, string message)
    {
        if (level < minimumLevel)
            return;

        lock (_sync)
        {
            Directory.CreateDirectory(_logsDir);
            RollIfNeeded();

            string line = $"{DateTime.UtcNow:O} [{level}] {message}{Environment.NewLine}";
            File.AppendAllText(_activeFilePath, line, Encoding.UTF8);
        }
    }

    private void RollIfNeeded()
    {
        if (!File.Exists(_activeFilePath))
            return;

        var fi = new FileInfo(_activeFilePath);
        if (fi.Length < MaxBytesPerFile)
            return;

        string rolledName = $"ub-Rythai-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log";
        string rolledPath = Path.Combine(_logsDir, rolledName);
        File.Move(_activeFilePath, rolledPath, overwrite: true);

        string[] rolled = Directory.GetFiles(_logsDir, "ub-Rythai-*.log");
        Array.Sort(rolled, StringComparer.OrdinalIgnoreCase);

        int extra = rolled.Length - MaxRolledFiles;
        for (int i = 0; i < extra; i++)
        {
            try
            {
                File.Delete(rolled[i]);
            }
            catch
            {
                // Best-effort retention cleanup; logging should continue even if deletion fails.
            }
        }
    }
}
