using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;

namespace VRChatOSCClient.OSCConnections;

internal static class MessageParser
{
    /// Example of data packages
    /// The first part is the address as string, Then a spacer, then the type of parameter in the second index, another spacer and then the value of the type.
    ///                                           /avatar/parameters/Tail_Angle                                             f      0.021788685     end padding
    /// ⊢—————————————————————————————————————————————————————————————————————————————————————————————————————————————⊣  ⊢————————⊣ ⊢———————————⊣ ⊢—————————————⊣
    /// 47 97 118 97 116 97 114 47 112 97 114 97 109 101 116 101 114 115 47 76 101 97 115 104 95 65 110 103 108 101 0 0 44 102 0 0 62 78 143 130 0 0 0 0 0 0 0 0

    /// <summary>
    /// 
    /// </summary>
    /// <param name="data"></param>
    /// <returns></returns>
    public static Message Parse(Memory<byte> data) {
        try {
            var stream = new SpanReader(data);
            var address = stream.ReadString();
            Span<byte> parametersTypes = stackalloc byte[stream.CountParameterTypes()];
            stream.ReadParameterType(parametersTypes);

            var parameters = new object?[parametersTypes.Length];

            for (var i = 0; i < parameters.Length; i++) {
                parameters[i] = parametersTypes[i] switch {
                    70 => false,
                    73 => float.PositiveInfinity,
                    84 => true,
                    83 or 115 => stream.ReadString(),
                    102 => stream.ReadFloat(),
                    105 => stream.ReadInt32(),
                    _ => null
                };
            }

            return new Message(address, [.. parameters]);
        }
        catch {
            Debug.WriteLine("Failed to parse OSC message");
            return new Message(string.Empty, []);
        }
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="message"></param>
    /// <returns></returns>
    public static Memory<byte> Serialize(Message message) {
        var writer = new SpanWriter(new byte[GetLength(message)]);
        writer.WriteString(message.Address);

        Span<byte> parameterTypes = stackalloc byte[message.Arguments.Length];
        //write parameter types
        for (var i = 0; i < message.Arguments.Length; i++) {
            var arg = message.Arguments[i];
            if(arg is null or not (string or float or int or bool)) {
                continue;
            }

            parameterTypes[i] = arg switch {
                string => 115, // 'S' for string
                float f => 102, // 'f' for float
                int => 105, // 'i' for int
                bool b => b ? (byte)84 : (byte)70, // 'T' for true, 'F' for false
                _ => throw new NotImplementedException(), // Should never happen because of the if statment above, but just in case.
            };
        }

        writer.WriteParameterType(parameterTypes);

        foreach (var param in message.Arguments) {
            if(param is null or not (string or float or int)) { //bools can be ignored
                continue;
            }
            
            switch (param) {
                case string str:
                    writer.WriteString(str);
                    break;
                case float f:
                    writer.WriteFloat(f);
                    break;
                case int i:
                    writer.WriteInt32(i);
                    break;
                default:
                    throw new NotImplementedException($"Unsupported parameter type: {param.GetType()}"); // Should never happen because of the if statement above, but just in case.
            }
        }

        return writer.GetFinishedMemory();
    }

    private static int GetLength(Message message) {
        var addressLength = (Encoding.ASCII.GetByteCount(message.Address) + 4) & ~3;
        var parameterTypesLength = (message.Arguments.Length + 5) & ~3; ;
        var parameterLength = message.Arguments.OfType<object>()
            .Sum(param => param switch
            {
                string str => (Encoding.ASCII.GetByteCount(str) + 4) & ~3,
                float f => 4,
                int i => 4,
                _ => 0
            });

        return addressLength + parameterTypesLength + parameterLength;
    }
}

public struct SpanReader(Memory<byte> buffer)
{
    private int _position = 0;
    private readonly Span<byte> Span => buffer.Span;
    public readonly int Length => buffer.Length;

    private readonly int IndexOf(byte value) => Span[_position..].IndexOf(value);

    private void CopyTo(Span<byte> destination) {
        Span[_position..(_position + destination.Length)].CopyTo(destination);
        _position += destination.Length;
    }

    public readonly int CountParameterTypes() {
        if (Span[_position] != 44) {
            return 0;
        }

        var nextIndex = IndexOf(0);
        return nextIndex - 1; //Remove 1 extra for the parameter identifier
    }
    public void ReadParameterType(Span<byte> bufferSpan) {
        _position += 1; // Skip the 44 identifier for parameter types
        CopyTo(bufferSpan);
        _position = (_position + 4) & ~3;
    }

    public string ReadString() {
        int nextIndex = IndexOf(0);
        Span<byte> bufferSpan = stackalloc byte[nextIndex];
        CopyTo(bufferSpan);

        var result = Encoding.ASCII.GetString(bufferSpan);
        _position = (_position + 4) & ~3;
        return result;
    }

    public int ReadInt32() {
        Span<byte> bufferSpan = stackalloc byte[4];
        CopyTo(bufferSpan);
        return BinaryPrimitives.ReadInt32BigEndian(bufferSpan);
    }

    public float ReadFloat() {
        Span<byte> bufferSpan = stackalloc byte[4];
        CopyTo(bufferSpan);
        return BinaryPrimitives.ReadSingleBigEndian(bufferSpan);
    }
}

public struct SpanWriter(Memory<byte> buffer) {
    private int _position = 0;
    private readonly Span<byte> Span => buffer.Span;

    public void WriteString(string input) {
        _position += Encoding.ASCII.GetBytes(input, Span[_position..]);
        _position = (_position + 4) & ~3;
    }

    public void WriteInt32(int value) {
        BinaryPrimitives.WriteInt32BigEndian(Span[_position..], value);
        _position += 4;
    }

    public void WriteFloat(float value) {
        BinaryPrimitives.WriteSingleBigEndian(Span[_position..], value);
        _position += 4;
    }

    public void WriteParameterType(ReadOnlySpan<byte> type) {
        Span[_position++] = 44; // 44 is the ASCII code for ','

        type.CopyTo(Span[_position..]);
        _position += type.Length;

        _position = (_position + 4) & ~3; // Align to 4 bytes
    }

    public readonly Memory<byte> GetFinishedMemory() => buffer[.._position];

    
}

file static class SpanExtensions
{
    extension(ReadOnlySpan<byte> span)
    {
        public int IndexOf(byte value) {
            for (var i = 0; i < span.Length; i++) {
                if (span[i] == value) {
                    return i;
                }
            }
            return -1;
        }
    }
}