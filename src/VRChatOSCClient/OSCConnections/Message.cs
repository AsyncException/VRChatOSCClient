namespace VRChatOSCClient.OSCConnections;
public record Message(string Address, object?[] Arguments);

public record ParameterChangedMessage(string Name, object Value, string Address, object?[] Arguments) : Message(Address, Arguments) {
    public const string PARAMETER_CHANGED_ADDRESS = "/avatar/parameters/";
    public ParameterChangedMessage(string Name, object Value) : this(Name, Value, string.Concat(PARAMETER_CHANGED_ADDRESS, Name), [Value]) {}
    public ParameterChangedMessage(Message Message) : this(
        Message.Address.StartsWith(PARAMETER_CHANGED_ADDRESS) ? Message.Address[PARAMETER_CHANGED_ADDRESS.Length..] : throw new ArgumentException("Address is not of a parameter", nameof(Message)),
        Message.Arguments.Length > 0 && Message.Arguments[0] is not null ? Message.Arguments[0]! : throw new ArgumentException("Not enough arguments or argument is null", nameof(Message)),
        Message.Address,
        Message.Arguments) {}
}

public record ChatMessage(string Message, bool BypassKeyboard, bool PlayNotification, string Address, object?[] Arguments) : Message(Address, Arguments) {
    public const string MESSAGE_BOX_ADDRESS = "/chatbox/input";
    public ChatMessage(string Message, bool BypassKeyboard = true, bool PlayNotification = false) : this(Message, BypassKeyboard, PlayNotification, MESSAGE_BOX_ADDRESS, [Message, BypassKeyboard, PlayNotification]) {}
}