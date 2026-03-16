namespace ResharperProfiler.Protocol;

public enum Phase : byte
{
    Startup,
    SolutionListenerReady,
    SaveCaches,
    DaemonFinished
}
