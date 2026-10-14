using System.Numerics;
using WindUpKey.Protocol;

namespace WindUpKey.Services;

/// <summary>
/// Where a character is, as <see cref="CallCorroboration"/> compares it with a Call's destination.
/// </summary>
/// <param name="TerritoryId">Their territory (0 when unknown: nothing is compared).</param>
/// <param name="ResidentialCity">
/// The Lifestream city id when the territory is a residential district, or a housing interior matched to its city
/// through its <c>PlaceNameRegion</c> (<c>HousingCallLocation.TryGetCityByPlaceNameRegion</c>); 0 anywhere else, or when
/// that city can't be resolved.
/// </param>
/// <param name="IsHousing">
/// 07-B6: the territory is a residential district or a housing interior (<c>TerritoryIntendedUse</c> HousingIndoor:
/// a house, an apartment, an apartment lobby), whether or not its city resolved.
/// </param>
public readonly record struct CallPlace(uint TerritoryId, int ResidentialCity, bool IsHousing = false);

/// <summary>The owner as the doll sees them in her object table right now.</summary>
public readonly record struct CallOwnerSighting(Vector3 Position, CallPlace Place);

/// <summary>
/// 07-B3 (D2's second half): checks a Call against what the doll can see of her owner, once, when it arrives. An owner in
/// her party must be in the destination's territory (for housing, in that city's district or one of its interiors);
/// an owner in her object table must be within <see cref="MaxVisibleDistanceYalms"/> of the destination (and in its
/// territory, which is hers). When neither can be checked the Call is accepted (only 07-A5's checks apply); so is a
/// housing Call against a housing territory whose city can't be resolved (07-B6). The debug recall skips it. Reasons carry no location and no key. No Dalamud dependency.
/// </summary>
public static class CallCorroboration
{
    /// <summary>How far the destination may be from a visible owner's live position.</summary>
    public const float MaxVisibleDistanceYalms = 50f;

    /// <summary>
    /// Whether <paramref name="call"/> agrees with <paramref name="ownerInParty"/> (the owner's party-list territory, or
    /// null when they aren't in her party) and <paramref name="ownerVisible"/> (null when she can't see them).
    /// </summary>
    public static CallValidation Check(CallPayload call, CallPlace? ownerInParty, CallOwnerSighting? ownerVisible)
    {
        if (ownerInParty is { } party && IsComparable(call, party) && !Matches(call, party))
            return CallValidation.Reject("owner's party zone differs");

        if (ownerVisible is { } seen)
        {
            if (IsComparable(call, seen.Place) && !Matches(call, seen.Place))
                return CallValidation.Reject("owner is visible elsewhere");

            // Not "> max": a NaN distance refuses too.
            var destination = new Vector3(call.X, call.Y, call.Z);
            if (!(Vector3.Distance(destination, seen.Position) <= MaxVisibleDistanceYalms))
                return CallValidation.Reject("owner is visible elsewhere");
        }

        return CallValidation.Accept;
    }

    /// <summary>
    /// Whether <paramref name="place"/> can be compared with the Call at all: not an unknown territory (0), and, 07-B6,
    /// for a housing Call not a housing territory whose city can't be resolved (an apartment lobby or interior outside the
    /// known <c>PlaceNameRegion</c> set). That is nothing to check, never a mismatch, so it can't cause a false refusal. A
    /// Call that isn't for housing still compares territory ids, which need no city.
    /// </summary>
    public static bool IsComparable(CallPayload call, CallPlace place) =>
        place.TerritoryId != 0 && !(IsHousingCall(call) && place is { IsHousing: true, ResidentialCity: 0 });

    /// <summary>
    /// Whether <paramref name="place"/> is the Call's destination territory: a housing Call by its city (the outdoor
    /// district or an interior of that city), any other Call by its territory id.
    /// </summary>
    public static bool Matches(CallPayload call, CallPlace place) =>
        IsHousingCall(call)
            ? place.ResidentialCity != 0 && place.ResidentialCity == call.HousingCity
            : place.TerritoryId == call.TerritoryId;

    private static bool IsHousingCall(CallPayload call) => call.HousingWard > 0 && call.HousingCity != 0;
}
