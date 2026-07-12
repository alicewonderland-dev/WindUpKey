using System;
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
