using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using VRChatOSCClient.OSCConnections;

namespace VRChatOSCClient;

public class MessageFilter
{
    /// <summary>
    /// Gets or sets a value indicating whether the system should receive all messages.
    /// </summary>
    public bool ReceiveMessages { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the system should receive parameter changes.
    /// </summary>
    public bool ReceiveParameterChanges { get; set; } = true;

    public Regex? ParameterPattern { get; private set; } = null;

    /// <summary>
    /// Set a pattern for filtering changing parameters. The parameters address will also have to match the <see cref="AddressPattern"/>.
    /// </summary>
    /// <param name="pattern"></param>
    public void SetParameterPattern([StringSyntax("Regex")] string pattern) => ParameterPattern = new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);
    internal bool IsParameterPatternMatch(ParameterChangedMessage message) => ParameterPattern is null || ParameterPattern.IsMatch(message.Name);

    public Regex? AddressPattern { get; private set; } = null;

    /// <summary>
    /// Set a pattern for filtering messages by their address. If the message is a <see cref="ParameterChangedMessage"/>, the address will also have to match the <see cref="ParameterPattern"/> if parameter changes are enabled."/>
    /// </summary>
    /// <param name="pattern"></param>
    public void SetAddressPattern([StringSyntax("Regex")] string pattern) => AddressPattern = new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);
    internal bool IsAddressPatternMatch(Message message) => AddressPattern is null || AddressPattern.IsMatch(message.Address);
}