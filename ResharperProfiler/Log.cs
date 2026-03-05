using ResharperProfiler.Protocol;

namespace ResharperProfiler;

internal static class Log
{
    private static readonly string? LogFilePath;
    private static Client? _pipeClient;

    static Log()
    {
        LogFilePath = Environment.GetEnvironmentVariable("RESHARPER_PROFILER_LOG_FILE");

        if (LogFilePath is not null)
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
}
