using System.IO.Pipes;

namespace ResharperProfiler.Protocol;

public class Client : PipeEndpoint
{
    public Client(string pipeName, int connectTimeoutMs = 3000)
        : base(Connect(pipeName, connectTimeoutMs))
    {
        Start();
    }

    private static NamedPipeClientStream Connect(string pipeName, int timeoutMs)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        pipe.Connect(timeoutMs);
        return pipe;
    }
}
