namespace ResharperProfiler.Protocol;

public enum Phase : byte
{
    SolutionListenerReady = 0,
    SaveCaches = 1,
    DaemonFinished = 2
}
