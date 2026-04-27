namespace ResharperProfiler.Protocol;

public enum Phase : byte
{
    Startup,
    SolutionListenerReady,
    SolutionLoaded,
    DaemonFinished,
    LateLoadTask,
    DaemonStarted
}
