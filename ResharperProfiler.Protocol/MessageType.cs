namespace ResharperProfiler.Protocol;

// Wire format (both directions):
//   [length:int32][timestamp:int64][type:byte][payload:byte[]]
// length = size of everything after the length field (9 + payload size)
// timestamp = Environment.TickCount64 at message creation

public enum MessageType : byte
{
    Log = 1,
    Phase = 2,
    UIFreeze = 3,
    TypingLatency = 5
}
