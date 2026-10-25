using System;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Plugin.Services;
using WindUpKey.Quest;
using WindUpKey.Services;

namespace WindUpKey.Ui;

/// <summary>
/// The Quests tab: the 4×4 sticker board, drawn from the game's own Wondrous Tails textures, and this reset's duties.
/// When a texture can't be loaded (a patch moved it) the board falls back to a plain drawn grid.
/// </summary>
public sealed class QuestBoardTab
{
    /// <summary>The sticker board frame in <c>ui/uld/WeeklyBingo.tex</c> (1× pixels; the _hr1 texture is twice the size).</summary>
    private const string BoardTexture = "ui/uld/WeeklyBingo_hr1.tex";

    private const string BoardTextureLow = "ui/uld/WeeklyBingo.tex";

    private static readonly Vector2 BoardTextureSize = new(664, 584);

    private static readonly Vector2 BoardFrameMin = new(10, 319);

    private const float BoardFrameSize = 200;

    /// <summary>The 4×4 grid inside the frame: its offset and cell size (1× pixels).</summary>
    private const float GridInset = 10;

    private const float CellSize = 45;

    /// <summary>The 16 stickers (60×60 each, 4×4) in the left half of <c>ui/uld/WeeklyBingoSticker.tex</c>.</summary>
    private const string StickerTexture = "ui/uld/WeeklyBingoSticker_hr1.tex";

    private const string StickerTextureLow = "ui/uld/WeeklyBingoSticker.tex";

    private static readonly Vector2 StickerTextureSize = new(480, 240);

    private const float StickerSize = 60;

    /// <summary>How long a cleared line stays on the board, fading.</summary>
    private static readonly TimeSpan ClearAnimation = TimeSpan.FromSeconds(1.6);

    private static readonly Vector4 DoneColor = new(0.4f, 0.85f, 0.45f, 1f);
    private static readonly Vector4 NoticeColor = new(1f, 0.55f, 0.2f, 1f);

    private readonly QuestBoardService _quests;
    private readonly ITextureProvider _textures;
    private QuestDifficulty? _rerollConfirm;

    /// <summary>The Information pop-out is open.</summary>
    private bool _infoOpen;

    /// <summary>Whether the board's text is for a doll: the role, or the debug preview's (display only).</summary>
    private readonly Func<bool> _showsAsDoll;

    /// <summary>A debug role preview: the board is shown, but rerolls are off.</summary>
    private readonly Func<bool> _viewOnly;

    /// <param name="showsAsDoll">For the board's text only; null follows the service's role.</param>
    /// <param name="viewOnly">True while nothing may be changed (a role preview); null never.</param>
    public QuestBoardTab(QuestBoardService quests, ITextureProvider textures, Func<bool>? showsAsDoll = null, Func<bool>? viewOnly = null)
    {
        _quests = quests;
        _textures = textures;
        _showsAsDoll = showsAsDoll ?? (() => quests.ForDoll);
        _viewOnly = viewOnly ?? (() => false);
    }

    public void Draw()
    {
        ImGui.Spacing();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Quest Board");
        ImGui.SameLine();
        if (ImGui.SmallButton("Information"))
            _infoOpen = !_infoOpen;
        ImGui.Separator();
        ImGui.Spacing();

        if (_quests.OnVacation && _showsAsDoll())
        {
            ImGui.TextColored(NoticeColor, "You're on vacation: the board is paused until you're back.");
            ImGui.Spacing();
        }
        else if (!_quests.IsRolled)
        {
            ImGui.TextDisabled("The board is getting its quests ready…");
            ImGui.Spacing();
        }

        var scale = ImGuiHelpers.GlobalScale;
        ImGui.BeginGroup();
        DrawBoard(BoardFrameSize * 1.25f * scale);
        ImGui.EndGroup();
        ImGui.SameLine(0, 16 * scale);
        ImGui.BeginGroup();
        DrawDuties();
        ImGui.EndGroup();

        DrawRerollConfirm();
    }

    private void DrawBoard(float size)
    {
        var origin = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new Vector2(size));
        var draw = ImGui.GetWindowDrawList();
        var unit = size / BoardFrameSize;

        if (TryGetWrap(BoardTexture, BoardTextureLow, out var board))
        {
            draw.AddImage(
                board.Handle,
                origin,
                origin + new Vector2(size),
                BoardFrameMin / BoardTextureSize,
                (BoardFrameMin + new Vector2(BoardFrameSize)) / BoardTextureSize);
        }
        else
        {
            draw.AddRectFilled(origin, origin + new Vector2(size), ImGui.GetColorU32(new Vector4(0.93f, 0.88f, 0.74f, 1f)), 6 * unit);
            for (var i = 0; i < StickerBoard.CellCount; i++)
            {
                var (min, max) = CellRect(origin, unit, i);
                var tint = (i / StickerBoard.Size + i % StickerBoard.Size) % 2 == 0 ? 0.85f : 0.92f;
                draw.AddRectFilled(min, max, ImGui.GetColorU32(new Vector4(tint, tint * 0.95f, 0.6f, 1f)));
            }
        }

        var stickers = _quests.Board.Stickers;
        var clear = _quests.LastLineClear;
        var fade = 0f;
        if (clear is not null)
        {
            var elapsed = Stopwatch.GetElapsedTime(clear.Timestamp);
            if (elapsed < ClearAnimation)
                fade = 1f - (float)(elapsed / ClearAnimation);
        }

        // A finished line flashes, and the board's stickers fade out as it clears.
        for (var i = 0; i < StickerBoard.CellCount; i++)
        {
            var (min, max) = CellRect(origin, unit, i);
            if (StickerBoard.HasSticker(stickers, i))
                DrawSticker(draw, i, min, max, 1f);
            else if (fade > 0 && StickerBoard.HasSticker(clear!.BoardBefore, i))
                DrawSticker(draw, i, min, max, fade);

            if (fade > 0 && StickerBoard.HasSticker(clear!.LineCells, i))
                draw.AddRectFilled(min, max, ImGui.GetColorU32(new Vector4(1f, 0.85f, 0.3f, 0.45f * fade)), 4 * unit);
        }

    }

    private void DrawSticker(ImDrawListPtr draw, int cell, Vector2 min, Vector2 max, float alpha)
    {
        var tint = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, alpha));
        if (TryGetWrap(StickerTexture, StickerTextureLow, out var wrap))
        {
            var uv = new Vector2(cell % StickerBoard.Size, cell / StickerBoard.Size) * StickerSize;
            draw.AddImage(wrap.Handle, min, max, uv / StickerTextureSize, (uv + new Vector2(StickerSize)) / StickerTextureSize, tint);
            return;
        }

        var center = (min + max) / 2;
        draw.AddCircleFilled(center, (max.X - min.X) * 0.38f, ImGui.GetColorU32(new Vector4(0.85f, 0.45f, 0.2f, alpha)));
    }

    private static (Vector2 Min, Vector2 Max) CellRect(Vector2 origin, float unit, int cell)
    {
        var col = cell % StickerBoard.Size;
        var row = cell / StickerBoard.Size;
        var min = origin + new Vector2(GridInset + col * CellSize, GridInset + row * CellSize) * unit;
        var inset = 2 * unit;
        return (min + new Vector2(inset), min + new Vector2(CellSize * unit - inset));
    }

    private bool TryGetWrap(string path, string fallback, out IDalamudTextureWrap wrap)
    {
        if (_textures.GetFromGame(path).TryGetWrap(out var hi, out _) && hi is not null)
        {
            wrap = hi;
            return true;
        }

        if (_textures.GetFromGame(fallback).TryGetWrap(out var low, out _) && low is not null)
        {
            wrap = low;
            return true;
        }

        wrap = null!;
        return false;
    }

    private void DrawDuties()
    {
        var now = DateTimeOffset.UtcNow;
        var daily = FormatDuration(QuestResetClock.NextDailyReset(now) - now);
        var weekly = FormatDuration(QuestResetClock.NextWeeklyReset(now) - now);
        DrawTier(QuestDifficulty.Daily, $"Daily — resets in {daily}");
        ImGui.Spacing();
        DrawTier(QuestDifficulty.Easy, $"Easy — resets in {weekly}");
        ImGui.Spacing();
        DrawTier(QuestDifficulty.Medium, $"Medium — resets in {weekly}");
        ImGui.Spacing();
        DrawTier(QuestDifficulty.Hard, $"Hard — resets in {weekly}");
    }

    private void DrawTier(QuestDifficulty difficulty, string heading)
    {
        ImGui.TextUnformatted(heading);
        if (difficulty is QuestDifficulty.Easy or QuestDifficulty.Medium or QuestDifficulty.Hard)
            DrawRerollButton(difficulty);
        ImGui.Separator();

        var slots = _quests.Board.SlotsOf(difficulty).ToList();
        if (slots.Count == 0)
        {
            ImGui.TextDisabled(difficulty is QuestDifficulty.Daily or QuestDifficulty.Easy
                ? "Nothing yet."
                : "None for your level yet (they start at level 50).");
            return;
        }

        foreach (var slot in slots)
            DrawSlot(slot);
    }

    /// <summary>
    /// 0.2.10.0: a slot shows only what it asks and its stickers and turns. How the board works is in the Information
    /// window; what changes a reward (and what doesn't count) is for players to find out, so nothing here says.
    /// </summary>
    private void DrawSlot(QuestSlot slot)
    {
        var label = SlotLabel(slot);
        DrawCheckBox(slot.Completed);
        ImGui.SameLine();
        if (slot.Completed)
            ImGui.TextColored(DoneColor, label);
        else
            ImGui.TextUnformatted(label);

        ImGui.SameLine();
        ImGui.TextDisabled(RewardLabel(slot));
    }

    /// <summary>
    /// A check box the height of a line of text: an empty outlined box, or a filled one with a check mark once done. Only
    /// drawn (the game marks a quest done, not the player).
    /// </summary>
    private static void DrawCheckBox(bool done)
    {
        var size = ImGui.GetTextLineHeight();
        var min = ImGui.GetCursorScreenPos() + new Vector2(0, 1);
        var max = min + new Vector2(size - 2, size - 2);
        var drawList = ImGui.GetWindowDrawList();
        var rounding = 3 * ImGuiHelpers.GlobalScale;
        if (done)
        {
            drawList.AddRectFilled(min, max, ImGui.GetColorU32(DoneColor), rounding);
            var check = FontAwesomeIcon.Check.ToIconString();
            ImGui.PushFont(UiBuilder.IconFont);
            var fontSize = (size - 2) * 0.7f;
            var glyph = ImGui.CalcTextSize(check) * (fontSize / ImGui.GetFontSize());
            drawList.AddText(UiBuilder.IconFont, fontSize, (min + max - glyph) / 2, ImGui.GetColorU32(new Vector4(0.08f, 0.08f, 0.08f, 1f)), check);
            ImGui.PopFont();
        }
        else
        {
            drawList.AddRect(min, max, ImGui.GetColorU32(ImGuiCol.TextDisabled), rounding, ImDrawFlags.None, 1.5f * ImGuiHelpers.GlobalScale);
        }

        ImGui.Dummy(new Vector2(size, size));
    }

    private string SlotLabel(QuestSlot slot) => slot.Kind switch
    {
        QuestSlotKind.Roulettes => $"Complete {slot.Target} roulettes ({slot.Progress}/{slot.Target})",
        QuestSlotKind.LevelingRoulette => "Leveling Roulette on a job below the level cap",
        QuestSlotKind.Unlock => $"Unlock and clear: {_quests.DutyName(slot.ContentFinderConditionId)}",
        _ => _quests.DutyName(slot.ContentFinderConditionId),
    };

    /// <summary>The tier's own stickers and wind, in turns (winders get no wind), never a slot's hidden multiplier.</summary>
    private string RewardLabel(QuestSlot slot)
    {
        var stickers = QuestRewardRules.StickersFor(slot.Difficulty);
        var noun = stickers == 1 ? "sticker" : "stickers";
        if (!_showsAsDoll())
            return $"({stickers} {noun})";
        return $"({stickers} {noun}, {QuestRewardRules.Turns(QuestRewardRules.BaseHoursFor(slot.Difficulty))})";
    }

    private void DrawRerollButton(QuestDifficulty difficulty)
    {
        ImGui.SameLine();
        var used = QuestRollRules.IsRerollUsed(_quests.Board, difficulty, DateTimeOffset.UtcNow);
        // A role preview shows the board and its Information, but can't reroll.
        var can = _quests.CanReroll(difficulty) && !_viewOnly();
        if (!can)
            ImGui.BeginDisabled();
        if (ImGui.SmallButton($"Reroll##reroll{difficulty}"))
            _rerollConfirm = difficulty;
        if (!can)
            ImGui.EndDisabled();

        ImGui.SameLine();
        var period = difficulty == QuestDifficulty.Easy ? "today" : "this week";
        if (used)
            ImGui.TextDisabled("(used)");
        else if (can)
            ImGui.TextDisabled($"(1 left {period})");
    }

    private void DrawRerollConfirm()
    {
        if (_rerollConfirm is not { } pending)
            return;

        // A confirm left open when a role preview starts is dropped: the preview changes nothing.
        if (_viewOnly())
        {
            _rerollConfirm = null;
            return;
        }

        ImGui.OpenPopup("RerollQuests###WindUpKeyRerollConfirm");
        var open = true;
        if (ImGui.BeginPopupModal("RerollQuests###WindUpKeyRerollConfirm", ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            var period = pending == QuestDifficulty.Easy ? "today's" : "this week's";
            ImGui.TextWrapped(
                $"Reroll the open {QuestLabel(pending)} duties? Completed ones stay. This uses {period} {QuestLabel(pending)} reroll.");
            ImGui.Spacing();
            if (ImGui.Button("Reroll", new Vector2(120, 0)))
            {
                _quests.TryReroll(pending);
                _rerollConfirm = null;
                ImGui.CloseCurrentPopup();
            }

            ImGui.SameLine();
            if (ImGui.Button("Cancel", new Vector2(120, 0)))
            {
                _rerollConfirm = null;
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }

        if (!open)
            _rerollConfirm = null;
    }

    /// <summary>
    /// The Information pop-out: how the board works, in turns. Drawn every frame by the Quests window or tab, so it stays open
    /// beside the board while the player looks around (and comes back with it). It never explains the hidden bonuses, the unsynced rules or the picks.
    /// </summary>
    public void DrawInfoWindow()
    {
        if (!_infoOpen)
            return;

        ImGui.SetNextWindowSize(new Vector2(460, 0) * ImGuiHelpers.GlobalScale, ImGuiCond.FirstUseEver);
        if (ImGui.Begin("Quest Board Information###WindUpKeyQuestInfo", ref _infoOpen, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 30);
            var doll = _showsAsDoll();
            ImGui.TextUnformatted("The board");
            ImGui.Separator();
            ImGui.TextWrapped(doll
                ? "Complete quests for stickers on the 4×4 board. Every completion also winds your key and gives a spare spring."
                : "Complete quests for stickers on the 4×4 board. Every completion also gives spare springs.");
            ImGui.TextWrapped(doll
                ? "Finish a line (a row, column or diagonal) for bonus wind and a spare spring. A line clears the whole board."
                : "Finish a line (a row, column or diagonal) for spare springs. A line clears the whole board.");
            if (doll)
                ImGui.TextWrapped("Wind is counted in turns of the key. Quest wind can take you past your max wind, up to twice it.");
            ImGui.Spacing();

            ImGui.TextUnformatted("Quests");
            ImGui.Separator();
            InfoRow("Daily", "Complete 2 roulettes; the same one may count twice.", QuestDifficulty.Daily, doll);
            InfoRow("Easy", "Dungeons, and guildhests you haven't done yet.", QuestDifficulty.Easy, doll);
            InfoRow("Medium", "Extreme trials.", QuestDifficulty.Medium, doll);
            InfoRow("Hard", "Savage raids.", QuestDifficulty.Hard, doll);
            ImGui.Spacing();

            ImGui.TextUnformatted("Resets and rerolls");
            ImGui.Separator();
            ImGui.TextWrapped("Daily resets every day. Easy, Medium and Hard reset every week.");
            ImGui.TextWrapped(
                "Rerolling replaces a tier's open quests; completed ones stay. Easy can be rerolled once a day, Medium and Hard once a week.");
            ImGui.PopTextWrapPos();
        }

        ImGui.End();
    }

    private static void InfoRow(string tier, string what, QuestDifficulty difficulty, bool doll)
    {
        var stickers = QuestRewardRules.StickersFor(difficulty);
        var noun = stickers == 1 ? "sticker" : "stickers";
        // Winders' springs are doubled by the relay.
        var reward = doll
            ? $"{stickers} {noun}, {QuestRewardRules.Turns(QuestRewardRules.BaseHoursFor(difficulty))} and a spare spring"
            : $"{stickers} {noun} and 2 spare springs";
        ImGui.TextWrapped($"{tier}: {what} ({reward}.)");
    }

    private static string QuestLabel(QuestDifficulty difficulty) => difficulty switch
    {
        QuestDifficulty.Daily => "Daily",
        QuestDifficulty.Easy => "Easy",
        QuestDifficulty.Medium => "Medium",
        QuestDifficulty.Hard => "Hard",
        _ => "None",
    };

    /// <summary>Time until a reset (never wind time).</summary>
    private static string FormatDuration(TimeSpan remaining)
    {
        if (remaining.TotalDays >= 1)
            return $"{(int)remaining.TotalDays}d {remaining.Hours}h";
        if (remaining.TotalHours >= 1)
            return $"{(int)remaining.TotalHours}h {remaining.Minutes}m";
        return $"{Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes))}m";
    }
}
