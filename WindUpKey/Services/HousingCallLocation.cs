using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace WindUpKey.Services;

/// <summary>
/// Snapshot of residential identity for Call travel. Housing wards and subdivisions share a
/// TerritoryType id, so ward + division must be compared separately from territory.
/// </summary>
public readonly struct HousingCallLocation
{
    /// <summary>Lifestream ResidentialAetheryteKind values (city aetheryte row ids).</summary>
    public const int CityLimsa = 8;
    public const int CityGridania = 2;
    public const int CityUldah = 9;
    public const int CityFoundation = 70;
    public const int CityKugane = 111;

    private static readonly HashSet<uint> ResidentialTerritories =
    [
        339, // Mist
        340, // Lavender Beds
        341, // Goblet
        641, // Shirogane
        979, // Empyreum
    ];

    public int City { get; init; }
    public int Ward { get; init; }
    public int Division { get; init; }
    public int Plot { get; init; }
    public int Apartment { get; init; }
    public bool IsApartment { get; init; }
    public bool Indoor { get; init; }
    public uint OutdoorTerritoryId { get; init; }

    public bool IsHousing => Ward > 0 && City != 0 && Division is 1 or 2;

    public static bool IsResidentialTerritory(uint territoryId) =>
        ResidentialTerritories.Contains(territoryId);

    public static int? TryGetCityForTerritory(uint territoryId) =>
        territoryId switch
        {
            339 => CityLimsa,
            340 => CityGridania,
            341 => CityUldah,
            641 => CityKugane,
            979 => CityFoundation,
            _ => null,
        };

    public static int? TryGetCityByPlaceNameRegion(uint placeNameRegionRowId) =>
        placeNameRegionRowId switch
        {
            22 => CityLimsa,
            23 => CityGridania,
            24 => CityUldah,
            25 => CityFoundation,
            2402 => CityKugane,
            _ => null,
        };

    public static uint? TryGetResidentialTerritoryForCity(int city) =>
        city switch
        {
            CityLimsa => 339,
            CityGridania => 340,
            CityUldah => 341,
            CityKugane => 641,
            CityFoundation => 979,
            _ => null,
        };

    /// <summary>Reads ward/division/plot from HousingManager. Safe no-op outside housing.</summary>
    public static unsafe bool TryRead(uint currentTerritoryId, TerritoryType? territoryRow, out HousingCallLocation location)
    {
        location = default;
        var h = HousingManager.Instance();
        if (h is null)
            return false;

        var wardIndex = h->GetCurrentWard();
        if (wardIndex < 0)
            return false;

        var division = h->GetCurrentDivision();
        if (division is not (1 or 2))
            return false;

        var rawPlot = h->GetCurrentPlot();
        var indoor = h->IsInside();
        var isApartment = rawPlot is -128 or -127;
        // ClientStructs: -128 main apt wing, -127 subdivision apt wing.
        if (rawPlot == -127)
            division = 2;
        else if (rawPlot == -128)
            division = 1;

        var city = TryGetCityForTerritory(currentTerritoryId)
                   ?? (territoryRow is { } row ? TryGetCityByPlaceNameRegion(row.PlaceNameRegion.RowId) : null);
        var outdoorTerritory = currentTerritoryId;
        if (indoor || !IsResidentialTerritory(currentTerritoryId))
        {
            var original = HousingManager.GetOriginalHouseTerritoryTypeId();
            if (original != 0)
            {
                outdoorTerritory = original;
                city ??= TryGetCityForTerritory(original);
            }
        }

        if (city is null or 0)
            return false;

        outdoorTerritory = TryGetResidentialTerritoryForCity(city.Value) ?? outdoorTerritory;

        var plot = 0;
        var apartment = 0;
        if (isApartment)
        {
            var room = h->GetCurrentRoom();
            if (room > 0)
                apartment = room;
        }
        else if (rawPlot >= 0)
        {
            plot = rawPlot + 1;
        }

        location = new HousingCallLocation
        {
            City = city.Value,
            Ward = wardIndex + 1,
            Division = division,
            Plot = plot,
            Apartment = apartment,
            IsApartment = isApartment,
            Indoor = indoor,
            OutdoorTerritoryId = outdoorTerritory,
        };
        return location.IsHousing;
    }
}
