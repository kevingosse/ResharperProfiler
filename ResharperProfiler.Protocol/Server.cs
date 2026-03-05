using System.IO.Pipes;

namespace ResharperProfiler.Protocol;

public class Server : PipeEndpoint
{
    public Server(string pipeName)
        : base(CreateAndWait(pipeName))
    {
    }

    private static NamedPipeServerStream CreateAndWait(string pipeName)
    {
        var pipe = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
            inBufferSize: 4096, outBufferSize: 4096);
        pipe.WaitForConnection();
        return pipe;
    }
}
