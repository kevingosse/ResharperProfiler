using ResharperProfiler.Protocol;

namespace ResharperProfiler;

internal static class Log
{
    private static readonly string? LogFilePath;
    private static readonly bool DebugEnabled;
    private static Client? _pipeClient;

    static Log()
    {
        LogFilePath = Environment.GetEnvironmentVariable("RESHARPER_PROFILER_LOG_FILE");
        DebugEnabled = Environment.GetEnvironmentVariable("RESHARPER_PROFILER_DEBUG_LOG") == "1";

        if (LogFilePath is not null && Environment.GetEnvironmentVariable("RESHARPER_PROFILER_LOG_APPEND") != "1")
        {
            // Truncate log file
            File.WriteAllText(LogFilePath, string.Empty);
        }
    }

    public static void SetPipeClient(Client? client)
    {
        _pipeClient = client;
    }

    public static void Write(string message)
    {
        if (LogFilePath is not null)
        {
            lock (LogFilePath)
            {
                File.AppendAllText(LogFilePath, $"{DateTime.Now:O} {message}{Environment.NewLine}");
            }
        }

        _pipeClient?.SendLog(message);
    }

    public static void Debug(string message)
    {
        if (DebugEnabled)
            Write(message);
    }
}
