using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;
using Lumina.Excel.Sheets;

namespace WindUpKey.Services;

/// <summary>Runs in-game text commands / emotes on the framework thread.</summary>
public sealed unsafe class GameCommandRunner(
    IPluginLog log,
    IDataManager data,
    IUnlockState unlockState,
    Configuration config)
{
    /// <summary>Emote sheet row for Sit (`/sit`). Prefer <see cref="ResolveSitEmoteIds"/> at runtime.</summary>
    public const ushort SitEmoteId = 13;

    /// <summary>Emote sheet row for Sit on Ground (`/groundsit`).</summary>
    public const ushort GroundSitEmoteId = 52;

    private const int UnlockedEmotesCacheMs = 5000;

    private IReadOnlyList<(ushort Id, string Name)>? _unlockedLoopingEmotesCache;
    private long _unlockedLoopingEmotesCacheAtMs = long.MinValue;

    /// <summary>
    /// Unlocked looping emotes the player can use (EmoteMode set, has text command, unlocked).
    /// Sit and Ground Sit are always included (they loop via sit state, not EmoteMode).
    /// Sorted by display name. Cached briefly (~5s) to avoid rescanning the Emote sheet every UI draw.
    /// </summary>
    public IReadOnlyList<(ushort Id, string Name)> GetUnlockedLoopingEmotes()
    {
        var nowMs = Environment.TickCount64;
        if (_unlockedLoopingEmotesCache is not null
            && nowMs - _unlockedLoopingEmotesCacheAtMs < UnlockedEmotesCacheMs)
            return _unlockedLoopingEmotesCache;

        var sheet = data.GetExcelSheet<Emote>();
        if (sheet is null)
            return [];

        var list = new List<(ushort Id, string Name)>();
        var seen = new HashSet<ushort>();
        foreach (var emote in sheet)
        {
            var name = emote.Name.ToString();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var forceSit = IsSitOrGroundSitEmote(emote, name);
            if (!forceSit)
            {
                if (!IsLoopingUsableEmote(emote))
                    continue;
                if (!IsEmoteUnlocked(emote))
                    continue;
            }

            var id = (ushort)emote.RowId;
            if (!seen.Add(id))
                continue;

            list.Add((id, name));
        }

        // Absolute fallback if localization/command lookup missed them.
        TryAddForcedSitEmote(sheet, list, seen, SitEmoteId, "Sit");
        TryAddForcedSitEmote(sheet, list, seen, GroundSitEmoteId, "Sit on Ground");

        _unlockedLoopingEmotesCache = list
            .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        _unlockedLoopingEmotesCacheAtMs = nowMs;
        return _unlockedLoopingEmotesCache;
    }

    private static bool IsSitOrGroundSitEmote(Emote emote, string name)
    {
        if (name.Equals("Sit", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Sit on Ground", StringComparison.OrdinalIgnoreCase))
            return true;

        return EmoteUsesTextCommand(emote, "sit") || EmoteUsesTextCommand(emote, "groundsit");
    }

    /// <summary>
    /// True when this emote's text command (or alias) is /sit or /groundsit (slash optional).
    /// </summary>
    private static bool EmoteUsesTextCommand(Emote emote, string commandWithoutSlash)
    {
        if (emote.TextCommand.RowId == 0)
            return false;

        try
        {
            var tc = emote.TextCommand.Value;
            return CommandTokenEquals(tc.Command.ToString(), commandWithoutSlash)
                   || CommandTokenEquals(tc.ShortCommand.ToString(), commandWithoutSlash)
                   || CommandTokenEquals(tc.Alias.ToString(), commandWithoutSlash)
                   || CommandTokenEquals(tc.ShortAlias.ToString(), commandWithoutSlash);
        }
        catch
        {
            return false;
        }
    }

    private static bool CommandTokenEquals(string? raw, string commandWithoutSlash)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var token = raw.Trim();
        if (token.StartsWith('/'))
            token = token[1..];

        // TextCommand may be "sit" or "sit motion" — match first token only.
        var space = token.IndexOf(' ');
        if (space > 0)
            token = token[..space];

        return token.Equals(commandWithoutSlash, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryAddForcedSitEmote(
        Lumina.Excel.ExcelSheet<Emote> sheet,
        List<(ushort Id, string Name)> list,
        HashSet<ushort> seen,
        ushort emoteId,
        string fallbackName)
    {
        if (!seen.Add(emoteId))
            return;

        var name = fallbackName;
        if (sheet.TryGetRow(emoteId, out var emote))
        {
            var sheetName = emote.Name.ToString();
            if (!string.IsNullOrWhiteSpace(sheetName))
                name = sheetName;
        }

        list.Add((emoteId, name));
    }

    /// <summary>Display name for an emote row, or null if missing.</summary>
    public string? GetEmoteName(ushort emoteId)
    {
        var sheet = data.GetExcelSheet<Emote>();
        if (sheet is null || !sheet.TryGetRow(emoteId, out var emote))
            return null;

        var name = emote.Name.ToString();
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    /// <summary>
    /// Play the configured lock emote (fallback Ground Sit). Returns false if the game is not ready yet.
    /// </summary>
    public bool TryExecuteLockEmote()
    {
        var id = ResolveLockEmoteId();
        return TryExecuteEmote(id);
    }

    /// <summary>Play the configured lock emote; ignores readiness (best-effort).</summary>
    public void ExecuteLockEmote() => TryExecuteLockEmote();

    private ushort ResolveLockEmoteId()
    {
        var id = config.EffectiveLockEmoteId;
        if (id != GroundSitEmoteId && IsEmoteIdUnlockedAndLooping(id))
            return id;

        return GroundSitEmoteId;
    }

    private bool IsEmoteIdUnlockedAndLooping(ushort emoteId)
    {
        var sheet = data.GetExcelSheet<Emote>();
        if (sheet is null || !sheet.TryGetRow(emoteId, out var emote))
            return emoteId is SitEmoteId or GroundSitEmoteId;

        var name = emote.Name.ToString();
        if (IsSitOrGroundSitEmote(emote, name ?? string.Empty))
            return true;

        return IsLoopingUsableEmote(emote) && IsEmoteUnlocked(emote);
    }

    private static bool IsLoopingUsableEmote(Emote emote) =>
        emote.EmoteMode.RowId != 0 && emote.TextCommand.RowId != 0;

    private bool IsEmoteUnlocked(Emote emote)
    {
        try
        {
            return unlockState.IsEmoteUnlocked(emote);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "WindUpKey: IUnlockState.IsEmoteUnlocked failed for {EmoteId}; falling back to UIState", emote.RowId);
            try
            {
                var uiState = UIState.Instance();
                return uiState != null && uiState->IsEmoteUnlocked((ushort)emote.RowId);
            }
            catch (Exception ex2)
            {
                log.Warning(ex2, "WindUpKey: UIState.IsEmoteUnlocked failed for {EmoteId}", emote.RowId);
                return false;
            }
        }
    }

    private bool TryExecuteEmote(ushort emoteId)
    {
        try
        {
            var manager = EmoteManager.Instance();
            if (manager == null)
                return false;

            if (!manager->CanExecuteEmote(emoteId))
                return false;

            manager->ExecuteEmote(emoteId);
            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "WindUpKey: failed to ExecuteEmote {EmoteId}", emoteId);
            return true; // don't spin forever on hard failures
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
