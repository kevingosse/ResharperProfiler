using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;

namespace ResharperProfiler.Protocol;

public abstract class PipeEndpoint : IDisposable
{
    private PipeStream? _pipe;
    private readonly BlockingCollection<byte[]> _sendQueue = new();

    public event Action<long, MessageType, byte[]>? MessageReceived;
    public event Action<string, Exception>? Error;
    public event Action? Disconnected;

    protected PipeEndpoint(PipeStream pipe)
    {
        _pipe = pipe;
    }

    public void Start()
    {
        new Thread(ReceiveLoop) { IsBackground = true, Name = "PipeReceive" }.Start();
        new Thread(SendLoop) { IsBackground = true, Name = "PipeSend" }.Start();
    }

    public void Send(MessageType type, Action<BinaryWriter>? writePayload = null)
    {
        if (_pipe is null)
        {
            return;
        }

        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(Environment.TickCount64);
            w.Write((byte)type);
            writePayload?.Invoke(w);
        }

        try
        {
            _sendQueue.Add(ms.ToArray());
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    public void SendLog(string message)
    {
        Send(MessageType.Log, w => w.Write(message));
    }

    public void SendSolutionLoaded()
    {
        Send(MessageType.SolutionLoaded);
    }

    public void SendUIFreeze(long durationInMs)
    {
        Send(MessageType.UIFreeze, b => b.Write(durationInMs));
    }

    public void Dispose()
    {
        var pipe = _pipe;
        _pipe = null;
        _sendQueue.CompleteAdding();
        pipe?.Dispose();
    }

    private void SendLoop()
    {
        try
        {
            foreach (var data in _sendQueue.GetConsumingEnumerable())
            {
                var pipe = _pipe;
                if (pipe is null)
                    break;

                pipe.Write(BitConverter.GetBytes(data.Length), 0, 4);
                pipe.Write(data, 0, data.Length);
            }
        }
        catch (Exception ex) when (ex is not ThreadAbortException)
        {
            _pipe = null;
            Error?.Invoke("SendLoop", ex);
        }
    }

    private void ReceiveLoop()
    {
        try
        {
            using var reader = new BinaryReader(_pipe!, Encoding.UTF8, leaveOpen: true);

            while (_pipe is { IsConnected: true })
            {
                var length = reader.ReadInt32();
                var timestamp = reader.ReadInt64();
                var type = (MessageType)reader.ReadByte();
                var payloadLength = length - 9;
                var payload = payloadLength > 0 ? reader.ReadBytes(payloadLength) : [];

                try
                {
                    MessageReceived?.Invoke(timestamp, type, payload);
                }
                catch (Exception ex)
                {
                    Error?.Invoke("MessageHandler", ex);
                }
            }
        }
        catch (EndOfStreamException)
        {
            // Other end closed the connection
        }
        catch (Exception ex)
        {
            Error?.Invoke("ReceiveLoop", ex);
        }
        finally
        {
            Disconnected?.Invoke();
        }
    }
}
