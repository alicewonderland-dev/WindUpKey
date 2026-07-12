using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;

namespace WindUpKey.Services;

/// <summary>
/// Colored plugin chat: pink brand tag, named UIColor palette, and <c:name>…</c> tags for RP lines.
/// </summary>
public static partial class PluginChat
{
    public const string MessageTag = "Wind-Up Key";

    /// <summary>Brand / RP accent (UIColor key). 541 is purple in the sheet — do not use it for pink.</summary>
    public const ushort Pink = 561;

    public const ushort Red = 518;
    public const ushort Orange = 500;
    public const ushort Yellow = 31;
    public const ushort Green = 45;
    public const ushort Blue = 37;
    public const ushort Purple = 541;
    public const ushort Grey = 3;
    public const ushort White = 1;

    /// <summary>Names documented for LowWindMessages.config authors.</summary>
    public static readonly IReadOnlyDictionary<string, ushort> NamedColors =
        new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase)
        {
            ["pink"] = Pink,
            ["red"] = Red,
            ["orange"] = Orange,
            ["yellow"] = Yellow,
            ["green"] = Green,
            ["blue"] = Blue,
            ["purple"] = Purple,
            ["grey"] = Grey,
            ["gray"] = Grey,
            ["white"] = White,
        };

    [GeneratedRegex(@"<c:([A-Za-z]+)>(.*?)</c>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ColorTagRegex();

    /// <summary>
    /// Build a SeString from text that may contain <c:name>…</c> tags.
    /// Untagged spans use <paramref name="defaultBodyColor"/> when set.
    /// Unknown color names: inner text is printed without color (tags stripped).
    /// </summary>
    public static SeString BuildSeString(string text, ushort? defaultBodyColor = null)
    {
        text ??= string.Empty;
        var builder = new SeStringBuilder();
        var regex = ColorTagRegex();
        var last = 0;

        foreach (Match match in regex.Matches(text))
        {
            if (match.Index > last)
                AppendSpan(builder, text[last..match.Index], defaultBodyColor);

            var name = match.Groups[1].Value;
            var inner = match.Groups[2].Value;
            if (NamedColors.TryGetValue(name, out var color))
                builder.AddUiForeground(inner, color);
            else
                AppendSpan(builder, inner, defaultBodyColor);

            last = match.Index + match.Length;
        }

        if (last < text.Length)
            AppendSpan(builder, text[last..], defaultBodyColor);

        return builder.Build();
    }

    private static void AppendSpan(SeStringBuilder builder, string span, ushort? color)
    {
        if (span.Length == 0)
            return;
        if (color is { } c)
            builder.AddUiForeground(span, c);
        else
            builder.AddText(span);
    }

    /// <summary>Normal chat with pink [Wind-Up Key] tag. Parses body color tags.</summary>
    public static void Print(IChatGui chat, string body, ushort? defaultBodyColor = null)
    {
        chat.Print(BuildSeString(body, defaultBodyColor), MessageTag, Pink);
    }

    /// <summary>Print body already built (no further tag parse).</summary>
    public static void Print(IChatGui chat, SeString body)
    {
        chat.Print(body, MessageTag, Pink);
    }

    /// <summary>Urgent/error channel with pink brand tag. Body uses channel styling unless colored.</summary>
    public static void PrintError(IChatGui chat, string body, ushort? defaultBodyColor = null)
    {
        chat.PrintError(BuildSeString(body, defaultBodyColor), MessageTag, Pink);
    }
}
