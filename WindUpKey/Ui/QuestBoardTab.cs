using System;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
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
    private static readonly Vector4 UnsyncedColor = new(1f, 0.55f, 0.2f, 1f);

    private readonly QuestBoardService _quests;
    private readonly ITextureProvider _textures;
    private QuestDifficulty? _rerollConfirm;

    public QuestBoardTab(QuestBoardService quests, ITextureProvider textures)
    {
        _quests = quests;
        _textures = textures;
    }

    public void Draw()
    {
        ImGui.Spacing();
        ImGui.TextUnformatted("Quest Board");
        ImGui.Separator();
        ImGui.TextWrapped(_quests.ForDoll
            ? "Clear duties for stickers: Easy duties give 1, Medium 2 and Hard 3, and every sticker winds your key. " +
              "Finish a line (row, column or diagonal) for bonus wind and a spare spring; that line is then wiped, and the " +
              "rest of the board stays. A first clear gives triple wind, and so does an Ultimate; a partner in your " +
              "party adds half again, or your owner doubles it. Quest wind can take you past your max wind hours, up to " +
              "twice the max. Easy duties reset daily (or as soon as you finish them all), Medium and Hard weekly."
            : "Clear duties for stickers: Easy duties give 1, Medium 2 and Hard 3. Finish a line (row, column or " +
              "diagonal) for 2 spare springs; that line is then wiped, and the rest of the board stays. Easy duties " +
              "reset daily (or as soon as you finish them all), Medium and Hard weekly.");
        ImGui.Spacing();

        if (_quests.OnVacation)
        {
            ImGui.TextColored(UnsyncedColor, "You're on vacation: the board is paused until you're back.");
            ImGui.Spacing();
        }
        else if (!_quests.IsRolled)
        {
            ImGui.TextDisabled("The board is getting today's duties ready…");
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

        // A finished line's stickers flash and fade out as it's wiped; the rest of the board stays.
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
        DrawTier(QuestDifficulty.Easy, $"Easy — resets in {FormatDuration(QuestResetClock.NextDailyReset(now) - now)}");
        ImGui.Spacing();
        DrawTier(QuestDifficulty.Medium, $"Medium — resets in {FormatDuration(QuestResetClock.NextWeeklyReset(now) - now)}");
        ImGui.Spacing();
        DrawTier(QuestDifficulty.Hard, $"Hard — resets in {FormatDuration(QuestResetClock.NextWeeklyReset(now) - now)}");
    }

    private void DrawTier(QuestDifficulty difficulty, string heading)
    {
        ImGui.TextUnformatted(heading);
        if (difficulty is QuestDifficulty.Medium or QuestDifficulty.Hard)
            DrawRerollButton(difficulty);
        ImGui.Separator();

        var slots = _quests.Board.SlotsOf(difficulty).ToList();
        if (slots.Count == 0)
        {
            ImGui.TextDisabled(difficulty == QuestDifficulty.Easy
                ? "Nothing yet."
                : "None for your level yet (they start at level 50).");
            return;
        }

        foreach (var slot in slots)
            DrawSlot(slot);
    }

    private void DrawSlot(QuestSlot slot)
    {
        var label = SlotLabel(slot);
        var reward = RewardLabel(slot);
        var msqOpen = slot.Kind == QuestSlotKind.Msq;
        var box = slot.Completed ? "[x]" : "[ ]";
        if (slot.CompletedUnsynced)
            ImGui.TextColored(UnsyncedColor, $"{box} {label} — done unsynced");
        else if (slot.Completed && !msqOpen)
            ImGui.TextColored(DoneColor, $"{box} {label}");
        else
            ImGui.TextUnformatted($"{box} {label}");

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(SlotTooltip(slot));

        ImGui.SameLine();
        ImGui.TextDisabled(reward);
    }

    private string SlotLabel(QuestSlot slot) => slot.Kind switch
    {
        QuestSlotKind.Msq => _quests.TrackedMsqName() is { } name ? $"Main scenario: {name}" : "Main scenario quest",
        QuestSlotKind.LevelingRoulette => "Leveling Roulette on a job below the level cap",
        QuestSlotKind.Unlock => $"Unlock and clear: {_quests.DutyName(slot.ContentFinderConditionId)}",
        _ => _quests.DutyName(slot.ContentFinderConditionId),
    };

    private string RewardLabel(QuestSlot slot)
    {
        // A winder's board pays stickers (and springs for lines), never wind.
        var doll = _quests.ForDoll;
        if (slot.Kind == QuestSlotKind.Msq)
        {
            if (!doll)
                return slot.Completed ? "(today's sticker done)" : "(1 sticker)";
            return slot.Completed
                ? $"({QuestRewardRules.MsqHours:0}h each turn-in; today's sticker done)"
                : $"(1 sticker, {QuestRewardRules.MsqHours:0}h each turn-in)";
        }

        // A slot done unsynced shows what it gave, not what a synced clear would have.
        var reward = QuestRewardRules.ForDutyClear(slot, synced: !slot.CompletedUnsynced);
        var noun = reward.Stickers == 1 ? "sticker" : "stickers";
        if (!doll)
            return $"({reward.Stickers} {noun})";
        return $"({reward.Stickers} {noun}, {reward.Hours:0}h{(slot.HalfWind ? ", half wind" : string.Empty)})";
    }

    private static string SlotTooltip(QuestSlot slot) => slot.Difficulty switch
    {
        QuestDifficulty.Easy when slot.Kind == QuestSlotKind.Msq =>
            "Every main scenario quest you turn in winds the key. The first one each day also gives a sticker.",
        QuestDifficulty.Easy =>
            "Must be done synced. Unrestricted Party counts only with Level Sync on; an unsynced clear doesn't count.",
        _ =>
            "Best done synced. Unsynced (Unrestricted Party without Level Sync), it gives a quarter of the wind and no stickers, and uses up the duty.",
    };

    private void DrawRerollButton(QuestDifficulty difficulty)
    {
        ImGui.SameLine();
        var used = QuestRollRules.IsRerollUsed(_quests.Board, difficulty);
        var can = _quests.CanReroll(difficulty);
        if (!can)
            ImGui.BeginDisabled();
        if (ImGui.SmallButton($"Reroll##reroll{difficulty}"))
            _rerollConfirm = difficulty;
        if (!can)
            ImGui.EndDisabled();

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(used
                ? "This week's reroll is used."
                : "Replaces the open duties of this tier (completed ones stay). One reroll a week.");
        }

        ImGui.SameLine();
        if (used)
            ImGui.TextDisabled("(used)");
        else if (can)
            ImGui.TextDisabled("(1 left this week)");
    }

    private void DrawRerollConfirm()
    {
        if (_rerollConfirm is not { } pending)
            return;

        ImGui.OpenPopup("RerollQuests###WindUpKeyRerollConfirm");
        var open = true;
        if (ImGui.BeginPopupModal("RerollQuests###WindUpKeyRerollConfirm", ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextWrapped(
                $"Reroll the open {QuestLabel(pending)} duties? Completed ones stay. This uses this week's {QuestLabel(pending)} reroll.");
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

    private static string QuestLabel(QuestDifficulty difficulty) => difficulty switch
    {
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
