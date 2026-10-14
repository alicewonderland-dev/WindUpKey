using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace WindUpKey.Services;

/// <summary>The client language, for the housing Yes/No table (Dalamud's <c>ClientLanguage</c> by name).</summary>
public enum YesnoLanguage
{
    Unknown,
    Japanese,
    English,
    German,
    French,
    ChineseSimplified,
    ChineseTraditional,
    Korean,
}

/// <summary>What the Call's own state says when a SelectYesno is visible (read on the framework thread).</summary>
/// <param name="ApartmentCall">The Call is for an apartment.</param>
/// <param name="NowMs">Monotonic now.</param>
/// <param name="EntranceInteractedMs">When the Call itself last interacted with an "Entrance" object, or null.</param>
/// <param name="InRightWard">She is in the Call's ward and division.</param>
/// <param name="LifestreamBusy">Lifestream is running a task (it selected the room).</param>
/// <param name="SelectRoomSeenMs">When <c>MansionSelectRoom</c> was last visible, or null.</param>
public readonly record struct HousingYesnoContext(
    bool ApartmentCall,
    long NowMs,
    long? EntranceInteractedMs,
    bool InRightWard,
    bool LifestreamBusy,
    long? SelectRoomSeenMs);

/// <summary>What to do with a visible SelectYesno.</summary>
public enum YesnoDecision
{
    /// <summary>Leave it alone.</summary>
    Ignore,

    /// <summary>Press Yes.</summary>
    Confirm,

    /// <summary>D10: an apartment confirm in a language with no known text: tell her to press Yes herself.</summary>
    Hint,
}

/// <summary>
/// 07-A6: which SelectYesno a Call may answer Yes to. A house entrance prompt must equal this client language's entry
/// exactly (all whitespace removed, U+00A0 and U+202F included; ordinal) and come within <see cref="EntranceWindowMs"/>
/// of the Call's own interaction with an "Entrance". An apartment prompt must match this language's pattern, for an
/// apartment Call in the right ward, while Lifestream is busy, with <c>MansionSelectRoom</c> seen within
/// <see cref="SelectRoomWindowMs"/>. Never <c>Contains</c>, never an empty prompt, never a loose word. No Dalamud
/// dependency: the caller passes the prompt's plain text.
/// </summary>
public static class HousingYesnoMatcher
{
    /// <summary>How soon after our own Entrance interaction its prompt is accepted.</summary>
    public const long EntranceWindowMs = 5_000;

    /// <summary>How recently the room list must have been visible for the apartment confirm.</summary>
    public const long SelectRoomWindowMs = 3_000;

    /// <summary>The longest prompt text a debug line may carry (<see cref="PromptLogDetail"/>).</summary>
    public const int LoggedPromptMaxChars = 80;

    /// <summary>The house entrance prompt per language (Lifestream, AutoRetainer and GagSpeak agree on these).</summary>
    private static readonly Dictionary<YesnoLanguage, string> HouseEnterConfirmText = new()
    {
        [YesnoLanguage.English] = "Enter the estate hall?",
        [YesnoLanguage.Japanese] = "「ハウス」へ入りますか？",
        [YesnoLanguage.ChineseSimplified] = "要进入这间房屋吗？",
        [YesnoLanguage.ChineseTraditional] = "要進入這間房屋嗎？",
        [YesnoLanguage.German] = "Das Gebäude betreten?",
        [YesnoLanguage.French] = "Entrer dans la maison ?",
        [YesnoLanguage.Korean] = "'주택'으로 들어가시겠습니까?",
    };

    /// <summary>
    /// The apartment room confirm per language, matched against the prompt with its whitespace collapsed to single
    /// spaces. Only EN and DE are known; the others get the hint (D10).
    /// </summary>
    private static readonly Dictionary<YesnoLanguage, Regex> ApartmentConfirmPattern = new()
    {
        [YesnoLanguage.English] = new Regex(@"^Enter .+?['’]s room\?$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)),
        [YesnoLanguage.German] = new Regex(@"^Wohnung Nr\.? .+? betreten\?$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)),
    };

    /// <summary>Dalamud's <c>ClientLanguage</c> name (or a fork's) to <see cref="YesnoLanguage"/>.</summary>
    public static YesnoLanguage FromClientLanguage(string? name) => name switch
    {
        "Japanese" => YesnoLanguage.Japanese,
        "English" => YesnoLanguage.English,
        "German" => YesnoLanguage.German,
        "French" => YesnoLanguage.French,
        "ChineseSimplified" => YesnoLanguage.ChineseSimplified,
        "ChineseTraditional" or "TraditionalChinese" => YesnoLanguage.ChineseTraditional,
        "Korean" => YesnoLanguage.Korean,
        _ => YesnoLanguage.Unknown,
    };

    /// <summary>The house entrance entry for <paramref name="language"/>, or null.</summary>
    public static string? HouseEntranceText(YesnoLanguage language) =>
        HouseEnterConfirmText.TryGetValue(language, out var text) ? text : null;

    /// <summary>True when an apartment confirm can be recognised in <paramref name="language"/>.</summary>
    public static bool HasApartmentPattern(YesnoLanguage language) => ApartmentConfirmPattern.ContainsKey(language);

    /// <summary>The prompt with every whitespace character removed (U+00A0 and U+202F are whitespace too).</summary>
    public static string RemoveWhitespace(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (!char.IsWhiteSpace(c) && c is not ('\u00A0' or '\u202F'))
                sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>The prompt with each whitespace run turned into one space, trimmed.</summary>
    public static string CollapseWhitespace(string text)
    {
        var sb = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c) || c is '\u00A0' or '\u202F')
            {
                pendingSpace = sb.Length > 0;
                continue;
            }

            if (pendingSpace)
                sb.Append(' ');
            pendingSpace = false;
            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>True when the Call may press Yes on a SelectYesno showing <paramref name="prompt"/>.</summary>
    public static bool ShouldConfirm(string? prompt, YesnoLanguage language, HousingYesnoContext context) =>
        Decide(prompt, language, context) == YesnoDecision.Confirm;

    /// <summary>What to do with a SelectYesno showing <paramref name="prompt"/> (its plain text).</summary>
    public static YesnoDecision Decide(string? prompt, YesnoLanguage language, HousingYesnoContext context)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return YesnoDecision.Ignore;

        if (!context.ApartmentCall)
        {
            if (HouseEntranceText(language) is not { } entry
                || !Within(context.EntranceInteractedMs, context.NowMs, EntranceWindowMs))
                return YesnoDecision.Ignore;

            return string.Equals(RemoveWhitespace(prompt), RemoveWhitespace(entry), StringComparison.Ordinal)
                ? YesnoDecision.Confirm
                : YesnoDecision.Ignore;
        }

        if (!context.InRightWard
            || !context.LifestreamBusy
            || !Within(context.SelectRoomSeenMs, context.NowMs, SelectRoomWindowMs))
            return YesnoDecision.Ignore;

        if (!ApartmentConfirmPattern.TryGetValue(language, out var pattern))
            return YesnoDecision.Hint;

        try
        {
            return pattern.IsMatch(CollapseWhitespace(prompt)) ? YesnoDecision.Confirm : YesnoDecision.Ignore;
        }
        catch (RegexMatchTimeoutException)
        {
            return YesnoDecision.Ignore;
        }
    }

    /// <summary>
    /// 07-A10 (04-D10): a prompt's raw text may go in the (debug-only) log only for an apartment Call in context (in the
    /// right ward, with the room list seen within <see cref="SelectRoomWindowMs"/>) in a client language with no known
    /// apartment pattern. Those are the texts that still need recording; accepted for now, until they are collected.
    /// </summary>
    public static bool MayLogPromptText(YesnoLanguage language, HousingYesnoContext context) =>
        context.ApartmentCall
        && context.InRightWard
        && Within(context.SelectRoomSeenMs, context.NowMs, SelectRoomWindowMs)
        && !HasApartmentPattern(language);

    /// <summary>
    /// 07-A10: what a log line may say about a prompt the Call didn't confirm: its text (truncated) when
    /// <see cref="MayLogPromptText"/>, otherwise only <c>len=N matched=no</c>.
    /// </summary>
    public static string PromptLogDetail(string? prompt, YesnoLanguage language, HousingYesnoContext context)
    {
        var text = prompt ?? string.Empty;
        if (text.Length == 0 || !MayLogPromptText(language, context))
            return $"len={text.Length} matched=no";

        return text.Length <= LoggedPromptMaxChars
            ? $"'{text}'"
            : $"'{text[..LoggedPromptMaxChars]}…'";
    }

    private static bool Within(long? atMs, long nowMs, long windowMs) =>
        atMs is { } at && nowMs >= at && nowMs - at <= windowMs;
}
