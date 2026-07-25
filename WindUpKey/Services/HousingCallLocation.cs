using System.Collections.Generic;
using Dalamud.Plugin.Services;
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

    /// <summary>
    /// Reads ward/division/plot from HousingManager. Inside private houses, GetCurrentWard often
    /// returns -1 — use <see cref="HousingManager.GetCurrentIndoorHouseId"/> / HouseId instead.
    /// Pass <paramref name="data"/> so indoor territories can resolve city via PlaceNameRegion.
    /// </summary>
    public static unsafe bool TryRead(
        uint currentTerritoryId,
        TerritoryType? territoryRow,
        out HousingCallLocation location,
        IDataManager? data = null)
    {
        location = default;
        var h = HousingManager.Instance();
        if (h is null)
            return false;

        var sheet = data?.GetExcelSheet<TerritoryType>();
        if (territoryRow is null && sheet?.TryGetRow(currentTerritoryId, out var currentRow) == true)
            territoryRow = currentRow;

        var indoor = h->IsInside();
        int ward;
        int division;
        var plot = 0;
        var apartment = 0;
        bool isApartment;
        var outdoorTerritory = currentTerritoryId;

        if (indoor)
        {
            if (!TryReadIndoor(h, currentTerritoryId, out ward, out division, out plot, out apartment, out isApartment, out outdoorTerritory))
                return false;
        }
        else
        {
            var wardIndex = h->GetCurrentWard();
            if (wardIndex < 0)
                return false;

            division = h->GetCurrentDivision();
            if (division is not (1 or 2))
                return false;

            var rawPlot = h->GetCurrentPlot();
            isApartment = rawPlot is -128 or -127;
            // ClientStructs: -128 main apt wing, -127 subdivision apt wing.
            if (rawPlot == -127)
                division = 2;
            else if (rawPlot == -128)
                division = 1;

            ward = wardIndex + 1;
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
        }

        var city = TryGetCityForTerritory(outdoorTerritory)
                   ?? TryGetCityForTerritory(currentTerritoryId)
                   ?? (territoryRow is { } row ? TryGetCityByPlaceNameRegion(row.PlaceNameRegion.RowId) : null);

        if ((city is null or 0) && sheet is not null
            && outdoorTerritory != 0
            && outdoorTerritory != currentTerritoryId
            && sheet.TryGetRow(outdoorTerritory, out var outdoorRow))
        {
            city = TryGetCityByPlaceNameRegion(outdoorRow.PlaceNameRegion.RowId)
                   ?? TryGetCityForTerritory(outdoorTerritory);
        }

        if (city is null or 0)
            return false;

        outdoorTerritory = TryGetResidentialTerritoryForCity(city.Value) ?? outdoorTerritory;
        if (!IsResidentialTerritory(outdoorTerritory))
        {
            var mapped = TryGetResidentialTerritoryForCity(city.Value);
            if (mapped is null)
                return false;
            outdoorTerritory = mapped.Value;
        }

        location = new HousingCallLocation
        {
            City = city.Value,
            Ward = ward,
            Division = division,
            Plot = plot,
            Apartment = apartment,
            IsApartment = isApartment,
            Indoor = indoor,
            OutdoorTerritoryId = outdoorTerritory,
        };
        return location.IsHousing;
    }

    private static unsafe bool TryReadIndoor(
        HousingManager* h,
        uint currentTerritoryId,
        out int ward,
        out int division,
        out int plot,
        out int apartment,
        out bool isApartment,
        out uint outdoorTerritory)
    {
        ward = 0;
        division = 0;
        plot = 0;
        apartment = 0;
        isApartment = false;
        outdoorTerritory = currentTerritoryId;

        var houseId = h->GetCurrentIndoorHouseId();
        if (houseId.Id == 0)
            houseId = h->GetCurrentHouseId();

        var original = HousingManager.GetOriginalHouseTerritoryTypeId();
        if (original != 0)
            outdoorTerritory = original;
        else if (houseId.TerritoryTypeId != 0)
            outdoorTerritory = houseId.TerritoryTypeId;

        // Manager ward is often -1 indoors; HouseId.WardIndex is the reliable source.
        var wardIndex = h->GetCurrentWard();
        if (wardIndex < 0 && houseId.Id != 0)
            wardIndex = (sbyte)houseId.WardIndex;
        if (wardIndex < 0)
            return false;

        ward = wardIndex + 1;

        var rawPlot = h->GetCurrentPlot();
        isApartment = rawPlot is -128 or -127 || (houseId.Id != 0 && houseId.IsApartment);

        if (isApartment)
        {
            if (rawPlot == -127)
                division = 2;
            else if (rawPlot == -128)
                division = 1;
            else if (houseId.Id != 0)
                division = houseId.ApartmentDivision + 1; // 0/1 → main/sub
            else
                division = h->GetCurrentDivision();

            if (division is not (1 or 2))
                return false;

            var room = h->GetCurrentRoom();
            if (room > 0)
                apartment = room;
            else if (houseId.Id != 0 && houseId.RoomNumber is > 0 and < 0x3FF)
                apartment = houseId.RoomNumber;
        }
        else
        {
            division = h->GetCurrentDivision();
            if (rawPlot >= 0)
            {
                plot = rawPlot + 1;
            }
            else if (houseId.Id != 0)
            {
                // HouseId plot index is 0-based; subdivision may be encoded as 30–59.
                var plotIndex = houseId.PlotIndex;
                if (plotIndex >= 30)
                {
                    division = 2;
                    plot = plotIndex - 30 + 1;
                }
                else
                {
                    plot = plotIndex + 1;
                    if (division is not (1 or 2))
                        division = 1;
                }
            }

            if (division is not (1 or 2))
                return false;
        }

        return ward > 0;
    }
}
