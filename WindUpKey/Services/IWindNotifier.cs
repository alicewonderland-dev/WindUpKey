using System;

namespace WindUpKey.Services;

/// <summary>Winder-only remaining-time feedback. Doll-facing code must not call this with remaining time.</summary>
public interface IWindNotifier
{
    void NotifyWinderRemaining(string targetIdentity, TimeSpan remaining);
    void NotifyWinderError(string message);
}

public sealed class ChatWindNotifier(Dalamud.Plugin.Services.IChatGui chat) : IWindNotifier
{
    public void NotifyWinderRemaining(string targetIdentity, TimeSpan remaining)
    {
        var display = WindTimerService.FormatRemaining(remaining);
        chat.Print($"[Wind-Up Key] {targetIdentity} now has {display} remaining.");
    }

    public void NotifyWinderError(string message)
    {
        chat.PrintError($"[Wind-Up Key] {message}");
    }
}
