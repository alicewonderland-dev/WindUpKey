#if WINDUP_TESTING || WINDUP_SELFTEST_UNITS
using System;
using System.Collections.Generic;
using System.Linq;

namespace WindUpKey.SelfTest;

/// <summary>A hook signature resolved with the signature scanner, without installing a hook (04-D8).</summary>
public sealed record SignatureFact(string Id, string Name, bool Resolved);

/// <summary>
/// A ClientStructs member-function address the plugin uses (04-D8): the lock hooks', and 06-C2 the emote cancel's.
/// <paramref name="WhenZero"/> says what happens without it, for the FAIL text (null: the generic text).
/// </summary>
public sealed record AddressFact(string Id, string Name, bool NonZero, string? WhenZero = null);

/// <summary>The last install result of one lock hook this session, from the diagnostic log (INFO).</summary>
public sealed record HookResultFact(string Name, bool Installed, DateTimeOffset AtUtc);

/// <summary>
/// One sheet row the plugin depends on (04-D8). <paramref name="Expectation"/> describes an extra condition (for
/// example a district's PlaceNameRegion) and <paramref name="ExpectationMet"/> whether it holds; a missing row is a
/// FAIL, an unmet expectation a WARN.
/// </summary>
public sealed record SheetRowFact(string Id, string Sheet, uint Row, string Label, bool Present, string? Expectation = null, bool ExpectationMet = true);

/// <summary>Whether a sheet could be opened at all.</summary>
public sealed record SheetFact(string Id, string Sheet, bool Available);

/// <summary>An IPC provider plugin: installed, and loaded.</summary>
public sealed record IpcFact(string Id, string Plugin, bool Installed, bool Loaded);

/// <summary>Everything the post-patch section looked at, collected once per run on the framework thread (04-D8).</summary>
public sealed record PostPatchFacts
{
    public IReadOnlyList<SignatureFact> Signatures { get; init; } = [];

    public IReadOnlyList<AddressFact> Addresses { get; init; } = [];

    public IReadOnlyList<HookResultFact> LastHookResults { get; init; } = [];

    public IReadOnlyList<SheetFact> Sheets { get; init; } = [];

    public IReadOnlyList<SheetRowFact> Rows { get; init; } = [];

    /// <summary>Emote rows the looping-emote query accepts (EmoteMode and TextCommand set), or null when the sheet is missing.</summary>
    public int? LoopingEmoteRows { get; init; }

    public IReadOnlyList<IpcFact> Ipc { get; init; } = [];

    /// <summary>What Call's own readiness check says (vnavmesh ready), or null when it wasn't asked.</summary>
    public bool? TravelReady { get; init; }
}

/// <summary>
/// Judges <see cref="PostPatchFacts"/> into <c>P-*</c> results (04-D8): a missing signature, a null address, a missing
/// sheet or row is a FAIL; an unmet row expectation a WARN; IPC is INFO, or SKIP when the plugin isn't installed;
/// hook-install history is INFO; what needs a game window is MANUAL. Pure.
/// </summary>
public static class PostPatchJudge
{
    public static IReadOnlyList<CheckResult> Judge(PostPatchFacts facts)
    {
        var results = new List<CheckResult>();

        foreach (var sig in facts.Signatures)
        {
            results.Add(sig.Resolved
                ? CheckResult.Pass(sig.Id, $"{sig.Name} signature resolves")
                : CheckResult.Fail(sig.Id, $"{sig.Name} signature not found: the movement lock hook would fail this patch"));
        }

        foreach (var address in facts.Addresses)
        {
            results.Add(address.NonZero
                ? CheckResult.Pass(address.Id, $"{address.Name} address is set")
                : CheckResult.Fail(address.Id, $"{address.Name} address is zero: {address.WhenZero ?? "ClientStructs doesn't know it this patch"}"));
        }

        results.Add(facts.LastHookResults.Count == 0
            ? CheckResult.Info("P-hook-last", "no lock hooks installed this session yet")
            : CheckResult.Info("P-hook-last", string.Join(", ", facts.LastHookResults
                .OrderBy(h => h.Name, StringComparer.Ordinal)
                .Select(h => $"{h.Name} {(h.Installed ? "installed" : "FAILED")}"))));

        // Sheets and rows, one result per id: a missing sheet or row fails it, an unmet expectation warns.
        var ids = facts.Sheets.Select(s => s.Id).Concat(facts.Rows.Select(r => r.Id)).Distinct(StringComparer.Ordinal).ToList();
        foreach (var id in ids)
        {
            var missingSheets = facts.Sheets.Where(s => s.Id == id && !s.Available).Select(s => s.Sheet).ToList();
            var rows = facts.Rows.Where(r => r.Id == id).ToList();
            var missingRows = rows.Where(r => !r.Present).ToList();
            var unmet = rows.Where(r => r.Present && !r.ExpectationMet).ToList();
            if (missingSheets.Count > 0)
                results.Add(CheckResult.Fail(id, $"sheet {string.Join(", ", missingSheets)} unavailable"));
            else if (missingRows.Count > 0)
                results.Add(CheckResult.Fail(id, "missing row(s): " + string.Join(", ", missingRows.Select(r => $"{r.Sheet}#{r.Row} ({r.Label})"))));
            else if (unmet.Count > 0)
                results.Add(CheckResult.Warn(id, "changed row(s): " + string.Join(", ", unmet.Select(r => $"{r.Sheet}#{r.Row} ({r.Label}): expected {r.Expectation}"))));
            else if (id == "P-data-emote" && facts.LoopingEmoteRows is 0)
                results.Add(CheckResult.Fail(id, "the looping-emote query matches no Emote row"));
            else
                results.Add(CheckResult.Pass(id, $"{rows.Count} row(s) present{(id == "P-data-emote" && facts.LoopingEmoteRows is { } n ? $", {n} looping emote row(s)" : string.Empty)}"));
        }

        foreach (var ipc in facts.Ipc)
        {
            if (!ipc.Installed)
            {
                results.Add(CheckResult.Skip(ipc.Id, $"{ipc.Plugin} isn't installed"));
                continue;
            }

            var ready = ipc.Id == "P-ipc-vnavmesh" && facts.TravelReady is { } travel
                ? travel ? ", travel ready" : ", not travel ready"
                : string.Empty;
            results.Add(CheckResult.Info(ipc.Id, $"{ipc.Plugin} installed, {(ipc.Loaded ? "loaded" : "not loaded")}{ready}"));
        }

        foreach (var manual in SelfTestCatalog.PostPatch.Where(e => e.ManualSteps.Count > 0))
            results.Add(SelfTestCatalog.ManualResult(manual));
        return results;
    }
}
#endif
