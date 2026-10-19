namespace RynthCore.Plugin.RynthChat;

internal sealed class ChatLine
{
    internal ulong  Seq       { get; init; }
    internal string Timestamp { get; init; } = "";
    internal string Channel   { get; init; } = "";
    /// <summary>AC's raw ChatMessageType (exported as "type" so engine rules can match on it).</summary>
    internal int    ChatType  { get; init; }
    internal string? Sender   { get; init; }
    internal string Text      { get; init; } = "";
    /// <summary>
    /// Automated system text (0.3.4): a reply to a plugin's own command, the login burst, or
    /// a line a plugin ate. Exported as "auto"; the engine shows it in the System tab only.
    /// Set under ChatBuffer's lock (NoteEaten can set it after Add).
    /// </summary>
    internal bool   Auto      { get; set; }
}
