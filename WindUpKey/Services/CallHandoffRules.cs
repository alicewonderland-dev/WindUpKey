namespace WindUpKey.Services;

/// <summary>
/// When an outdoor housing Call hands the last stretch from Lifestream to vnavmesh. Lifestream drives to the plot,
/// which can sit past the owner; once the doll is near the owner inside the ward, Lifestream is aborted and vnavmesh
/// paths to the owner directly, so the doll doesn't walk past them and come back. No Dalamud dependency.
/// </summary>
public static class CallHandoffRules
{
    /// <summary>Within this XZ distance of the owner, inside the owner's ward, navmesh takes over from Lifestream.</summary>
    public const float HandoffYalms = 30f;

    public static bool ShouldHandOffToNavmesh(
        bool housingCall,
        bool indoor,
        bool atHousingWard,
        bool betweenAreas,
        bool casting,
        float distanceToOwnerYalms) =>
        housingCall
        && !indoor
        && atHousingWard
        && !betweenAreas
        && !casting
        && distanceToOwnerYalms <= HandoffYalms;
}
