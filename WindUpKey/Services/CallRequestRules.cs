#if WINDUP_TESTING || WINDUP_CALL_UNITS
using System;
using System.Collections.Generic;
using WindUpKey.Protocol;

namespace WindUpKey.Services;

/// <summary>A World row as <see cref="CallRequestRules"/> needs it.</summary>
public readonly record struct CallWorldFacts(bool IsPublic, uint DataCenterId);

/// <summary>
/// A TerritoryType row as <see cref="CallRequestRules"/> needs it, with its Map's <c>SizeFactor</c> and offsets
/// (0 when it has no map).
/// </summary>
public readonly record struct CallTerritoryFacts(
    uint ContentFinderCondition,
    uint QuestBattle,
    ushort MapSizeFactor,
    short MapOffsetX,
    short MapOffsetY);

/// <summary>The game sheets behind <see cref="CallRequestRules.Validate"/>: Lumina in the plugin, a fake in tests.</summary>
public interface ICallSheetFacts
{
    bool TryGetWorld(uint worldId, out CallWorldFacts world);

    bool TryGetTerritory(uint territoryId, out CallTerritoryFacts territory);

    /// <summary>The Aetheryte sheet has an <c>IsAetheryte</c> row in this territory.</summary>
    bool HasAetheryte(uint territoryId);
}

/// <summary>What the doll knows of herself when a Call arrives.</summary>
/// <param name="CurrentWorldId">Her current world (0 when unknown, for example while loading).</param>
/// <param name="CurrentTerritoryId">
/// 07-A10: her current territory (0 when unknown). A Call into it needs no aetheryte there: she paths locally.
/// </param>
public readonly record struct CallDollContext(uint CurrentWorldId, uint CurrentTerritoryId);

/// <summary>The verdict on a Call's destination: <see cref="Ok"/>, or refused with a short, location-free reason.</summary>
public readonly record struct CallValidation(bool Ok, string Reason)
{
    public static readonly CallValidation Accept = new(true, string.Empty);

    public static CallValidation Reject(string reason) => new(false, reason);
}

/// <summary>
/// 07-A5 (D2's sanity half, D9, K1): whether a Call's destination is one the doll may travel to, from the payload's
/// numbers only (the payload's <c>WorldName</c> is never read: travel resolves the name from <c>WorldId</c>). A refusal
/// ends the Call as failed. The debug Store/Recall goes through it too. Reasons carry no location. No Dalamud dependency;
/// the sheets sit behind <see cref="ICallSheetFacts"/>.
/// </summary>
public static class CallRequestRules
{
    /// <summary>The wards per residential district today (since 6.x).</summary>
    public const int CurrentHousingWards = 30;

    /// <summary>The highest ward accepted: generous, so a future ward expansion doesn't refuse every Call.</summary>
    public const int MaxHousingWard = 40;

    /// <summary>House plots per division; Lifestream numbers the subdivision's 31–60.</summary>
    public const int PlotsPerDivision = 30;

    public const int MaxHousingPlot = 60;

    public const int MaxApartment = 999;

    /// <summary>Added to the map bound of an outdoor position (map edges are not exact).</summary>
    public const float PositionMarginYalms = 64f;

    /// <summary>The largest |y| accepted for an outdoor position.</summary>
    public const float MaxAbsHeight = 2048f;

    /// <summary>
    /// The smallest |x|/|z| bound in a residential district. Their territory's map covers the main division only; the
    /// subdivision's plots lie up to ~900 yalms out (Lifestream's plot entrance data), so the bound is at least this.
    /// </summary>
    public const float ResidentialCoordinateBound = 1024f;

    /// <summary>Lifestream's residential city (aetheryte) ids and their outdoor district, as <c>HousingCallLocation</c> maps them.</summary>
    public static readonly IReadOnlyDictionary<int, uint> ResidentialTerritoryByCity = new Dictionary<int, uint>
    {
        [8] = 339, // Mist (Limsa Lominsa)
        [2] = 340, // The Lavender Beds (Gridania)
        [9] = 341, // The Goblet (Ul'dah)
        [111] = 641, // Shirogane (Kugane)
        [70] = 979, // Empyreum (Ishgard)
    };

    /// <summary>Whether a Call to <paramref name="call"/> may be answered by a doll in <paramref name="doll"/>.</summary>
    public static CallValidation Validate(CallPayload call, CallDollContext doll, ICallSheetFacts sheets)
    {
        // World: a public world on her current data center.
        if (call.WorldId == 0 || !sheets.TryGetWorld(call.WorldId, out var world))
            return CallValidation.Reject("unknown world");
        if (!world.IsPublic)
            return CallValidation.Reject("world isn't public");
        if (doll.CurrentWorldId == 0 || !sheets.TryGetWorld(doll.CurrentWorldId, out var current))
            return CallValidation.Reject("own world unknown");
        if (world.DataCenterId == 0 || world.DataCenterId != current.DataCenterId)
            return CallValidation.Reject("another data center");

        // Territory: no duty and no quest battle (matching the owner's origin gate).
        if (call.TerritoryId == 0 || !sheets.TryGetTerritory(call.TerritoryId, out var territory))
            return CallValidation.Reject("unknown territory");
        if (territory.ContentFinderCondition != 0)
            return CallValidation.Reject("duty territory");
        if (territory.QuestBattle != 0)
            return CallValidation.Reject("quest battle territory");

        // 07-A10: the division as travel reads it, before it is checked.
        var division = NormalisedDivision(call);
        var housing = ValidateHousing(call, division);
        if (!housing.Ok)
            return housing;

        // 07-A10: a Call into her own territory paths there locally, so an aethernet-only city (Old Gridania, the
        // Firmament...) with no IsAetheryte row is fine; anywhere else she must be able to teleport to.
        var isHousing = IsHousing(call, division);
        if (!isHousing
            && call.TerritoryId != doll.CurrentTerritoryId
            && !sheets.HasAetheryte(call.TerritoryId))
            return CallValidation.Reject("no aetheryte in that territory");

        // Position: finite; outdoors within the map, with a margin. Interior coordinates aren't bounded.
        if (!float.IsFinite(call.X) || !float.IsFinite(call.Y) || !float.IsFinite(call.Z))
            return CallValidation.Reject("position isn't finite");
        if (!(isHousing && call.HousingIndoor))
        {
            var (boundX, boundZ) = OutdoorBounds(territory, isHousing);
            if (Math.Abs(call.X) > boundX || Math.Abs(call.Z) > boundZ || Math.Abs(call.Y) > MaxAbsHeight)
                return CallValidation.Reject("position out of bounds");
        }

        return CallValidation.Accept;
    }

    /// <summary>
    /// D9: an indoor house Call needs a known plot (1–60); an apartment, or an outdoor Call, doesn't. The owner's origin
    /// gate refuses to send one without it, and the doll refuses one from an older owner.
    /// </summary>
    public static bool IsIndoorPlotKnown(bool indoor, bool isApartment, int plot) =>
        !indoor || isApartment || plot is >= 1 and <= MaxHousingPlot;

    /// <summary>The coarse kind a forced log line may carry (07-A8): never an address or coordinates.</summary>
    public static string CoarseKind(bool isHousing, bool indoor, bool isApartment) =>
        !isHousing ? "field"
        : isApartment ? "apartment"
        : indoor ? "housing indoor"
        : "housing outdoor";

    /// <summary>
    /// 07-A10: the payload's division as travel reads it (<c>HousingCallLocation.EffectiveDivision</c>): a house plot
    /// above <see cref="PlotsPerDivision"/> is in the subdivision whatever division the payload says (the owner's indoor
    /// manager-API fallback sends division 1 with Lifestream's 31–60 plot). Anything else is checked as sent.
    /// </summary>
    public static int NormalisedDivision(CallPayload call) =>
        !call.HousingIsApartment && call.HousingPlot > PlotsPerDivision ? 2 : call.HousingDivision;

    /// <summary>The payload carries a housing address (all its housing fields consistent and set), with <paramref name="division"/> normalised.</summary>
    private static bool IsHousing(CallPayload call, int division) =>
        call.HousingWard > 0 && call.HousingCity != 0 && division is 1 or 2;

    private static bool IsHousingEmpty(CallPayload call) =>
        call.HousingCity == 0
        && call.HousingWard == 0
        && call.HousingDivision == 0
        && call.HousingPlot == 0
        && call.HousingApartment == 0
        && !call.HousingIsApartment
        && !call.HousingIndoor;

    private static CallValidation ValidateHousing(CallPayload call, int division)
    {
        if (IsHousingEmpty(call))
            return CallValidation.Accept;

        if (!ResidentialTerritoryByCity.TryGetValue(call.HousingCity, out var district))
            return CallValidation.Reject("unknown housing city");
        if (call.TerritoryId != district)
            return CallValidation.Reject("territory isn't that city's district");
        if (call.HousingWard is < 1 or > MaxHousingWard)
            return CallValidation.Reject("ward out of range");
        if (division is not (1 or 2))
            return CallValidation.Reject("division out of range");
        if (call.HousingPlot is < 0 or > MaxHousingPlot)
            return CallValidation.Reject("plot out of range");
        // A plot above 30 is in the subdivision by NormalisedDivision, so it can't contradict the division.
        if (call.HousingApartment is < 0 or > MaxApartment)
            return CallValidation.Reject("apartment out of range");
        if (call.HousingIsApartment && call.HousingPlot != 0)
            return CallValidation.Reject("apartment with a plot");
        if (!call.HousingIsApartment && call.HousingApartment != 0)
            return CallValidation.Reject("apartment number on a house");
        if (!IsIndoorPlotKnown(call.HousingIndoor, call.HousingIsApartment, call.HousingPlot))
            return CallValidation.Reject("indoor house with an unknown plot");
        return CallValidation.Accept;
    }

    /// <summary>
    /// |x| and |z| bounds: the map's half-width in yalms (1024·100/SizeFactor) plus its offset and the margin; at least
    /// <see cref="ResidentialCoordinateBound"/> in a residential district.
    /// </summary>
    private static (float X, float Z) OutdoorBounds(CallTerritoryFacts territory, bool residential)
    {
        var sizeFactor = territory.MapSizeFactor == 0 ? 100f : territory.MapSizeFactor;
        var half = 1024f * 100f / sizeFactor;
        var x = half + Math.Abs((float)territory.MapOffsetX) + PositionMarginYalms;
        var z = half + Math.Abs((float)territory.MapOffsetY) + PositionMarginYalms;
        if (residential)
        {
            x = Math.Max(x, ResidentialCoordinateBound + PositionMarginYalms);
            z = Math.Max(z, ResidentialCoordinateBound + PositionMarginYalms);
        }

        return (x, z);
    }
}
#endif
