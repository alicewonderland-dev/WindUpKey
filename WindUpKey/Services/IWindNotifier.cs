namespace WindUpKey.Services;

/// <summary>
/// Feedback for the player who wound, unwound or repaired a doll. Never exact time: only a percentage of the doll's
/// normal wind (overwind reads 100%) and whether they're overwound or broken.
/// </summary>
public interface IWindNotifier
{
    void NotifyWindResult(string targetIdentity, int percent, bool overwound, bool broken, bool brokeNow);
    void NotifyUnwound(string targetIdentity);
    void NotifyRepaired(string targetIdentity, bool stillBroken);
    void NotifyWinderError(string message);
}

public sealed class ChatWindNotifier(Dalamud.Plugin.Services.IChatGui chat) : IWindNotifier
{
    public void NotifyWindResult(string targetIdentity, int percent, bool overwound, bool broken, bool brokeNow)
    {
        if (brokeNow)
        {
            PluginChat.Print(chat, $"You wound {targetIdentity} too far, and something inside them snapped. They're broken until repaired.", PluginChat.Orange);
            return;
        }

        var state = broken ? " They're broken." : overwound ? " They're overwound; more winding may break them." : string.Empty;
        PluginChat.Print(chat, $"{targetIdentity} is now at {percent}% wind.{state}", PluginChat.Blue);
    }

    public void NotifyUnwound(string targetIdentity) =>
        PluginChat.Print(chat, $"You unwound {targetIdentity}.", PluginChat.Blue);

    public void NotifyRepaired(string targetIdentity, bool stillBroken) =>
        PluginChat.Print(
            chat,
            stillBroken ? $"{targetIdentity} couldn't be repaired." : $"{targetIdentity} is in working order.",
            PluginChat.Blue);

    public void NotifyWinderError(string message)
    {
        PluginChat.PrintError(chat, message);
    }
}
