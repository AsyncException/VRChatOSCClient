namespace VRChatOSCClient.OSCConnections;
public record Message(string Address, object?[] Arguments);

public record ParameterChangedMessage(string Name, object Value, string Address, object?[] Arguments) : Message(Address, Arguments) {
    public const string PARAMETER_CHANGED_ADDRESS = "/avatar/parameters/";
    public ParameterChangedMessage(string name, object value) : this(name, value, string.Concat(PARAMETER_CHANGED_ADDRESS, name), [value]) {}
    public ParameterChangedMessage(Message message) : this(
        message.Address.StartsWith(PARAMETER_CHANGED_ADDRESS) ? message.Address[PARAMETER_CHANGED_ADDRESS.Length..] : throw new ArgumentException("Address is not of a parameter", nameof(message)),
        message.Arguments.Length > 0 && message.Arguments[0] is not null ? message.Arguments[0]! : throw new ArgumentException("Not enough arguments or argument is null", nameof(message)),
        message.Address,
        message.Arguments) {}
}

public record ChatMessage(string Message, bool BypassKeyboard, bool PlayNotification, string Address, object?[] Arguments) : Message(Address, Arguments) {
    public const string MESSAGE_BOX_ADDRESS = "/chatbox/input";
    public ChatMessage(string message, bool bypassKeyboard = true, bool playNotification = false) : this(message, bypassKeyboard, playNotification, MESSAGE_BOX_ADDRESS, [message, bypassKeyboard, playNotification]) {}
}

public record AvatarChangedMessage(string AvatarId, string Address, object?[] Arguments) : Message(Address, Arguments) {
    public const string AVATAR_CHANGED_ADDRESS = "/avatar/change";
    public AvatarChangedMessage(Message message) : this((string)(message.Arguments[0] ?? string.Empty), message.Address, message.Arguments) { }
}