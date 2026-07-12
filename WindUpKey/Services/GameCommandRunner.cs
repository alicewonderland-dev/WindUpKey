using System;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;
using Dalamud.Plugin.Services;

namespace WindUpKey.Services;

/// <summary>Runs in-game text commands (e.g. emotes) on the framework thread.</summary>
public sealed unsafe class GameCommandRunner(IPluginLog log)
{
    /// <summary>Sit on Ground (`/groundsit`; same emote as /sitground).</summary>
    public const string SitGround = "/groundsit";

    /// <summary>Play Dead (`/playdead`). Prefers this for lock-down when unlocked.</summary>
    public const string PlayDead = "/playdead";

    /// <summary>Emote sheet row for Play Dead.</summary>
    private const ushort PlayDeadEmoteId = 143;

    /// <summary>Unlock link for Play Dead (Ballroom Etiquette - A Fitting End).</summary>
    private const uint PlayDeadUnlockLink = 330;

    /// <summary>
    /// Emote used when the doll is locked: /playdead if unlocked, otherwise /groundsit.
    /// </summary>
    public string GetLockEmoteCommand() =>
        IsPlayDeadUnlocked() ? PlayDead : SitGround;

    private bool IsPlayDeadUnlocked()
    {
        try
        {
            var uiState = UIState.Instance();
            if (uiState == null)
                return false;

            // Unlock-link check avoids an EXD get; IsEmoteUnlocked is the fallback.
            if (uiState->IsUnlockLinkUnlocked(PlayDeadUnlockLink))
                return true;

            return uiState->IsEmoteUnlocked(PlayDeadEmoteId);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "WindUpKey: failed to check Play Dead unlock; falling back to groundsit");
            return false;
        }
    }

    /// <summary>Returns false if the UI shell is not ready yet (caller may retry).</summary>
    public bool TryExecute(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return true;

        try
        {
            var shell = RaptureShellModule.Instance();
            var ui = UIModule.Instance();
            if (shell == null || ui == null)
                return false;

            var str = Utf8String.FromString(command.Trim());
            try
            {
                shell->ExecuteCommandInner(str, ui);
            }
            finally
            {
                str->Dtor(true);
            }

            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "WindUpKey: failed to execute {Command}", command);
            return true; // don't spin forever on hard failures
        }
    }

    public void Execute(string command) => TryExecute(command);
}
