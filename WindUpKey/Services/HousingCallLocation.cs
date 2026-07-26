using System.Collections.Generic;
using System.Text;
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

    /// <summary>
    /// Lifestream house plot index 1–60. Subdivision plots are 31–60 (not 1–30 + a division flag).
    /// </summary>
    public static int ToLifestreamPlot(int plot, int division, bool isApartment)
    {
        if (isApartment || plot <= 0)
            return plot;
        if (plot is >= 1 and <= 30 && division == 2)
            return plot + 30;
        return plot;
    }

    /// <summary>Division implied by a 1–60 Lifestream plot (or an explicit division when plot ≤ 30).</summary>
    public static int EffectiveDivision(int plot, int division, bool isApartment)
    {
        if (!isApartment && plot > 30)
            return 2;
        return division is 1 or 2 ? division : 1;
    }

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
    /// Reads ward/division/plot from HousingManager. Indoors, prefer
    /// <c>IndoorTerritory-&gt;HouseId</c> (ward/plot/room packed) — GetCurrentWard/Plot are often -1.
    /// Pass <paramref name="data"/> so indoor territories can resolve city via PlaceNameRegion.
    /// </summary>
    public static unsafe bool TryRead(
        uint currentTerritoryId,
        TerritoryType? territoryRow,
        out HousingCallLocation location,
        IDataManager? data = null,
        StringBuilder? diag = null)
    {
        location = default;
        var h = HousingManager.Instance();
        if (h is null)
        {
            diag?.Append("HousingManager null");
            return false;
        }

        var sheet = data?.GetExcelSheet<TerritoryType>();
        if (territoryRow is null && sheet?.TryGetRow(currentTerritoryId, out var currentRow) == true)
            territoryRow = currentRow;

        var indoor = h->IsInside() || h->IndoorTerritory is not null;
        diag?.Append($"terr={currentTerritoryId} isInside={h->IsInside()} indoorTerr={(h->IndoorTerritory is null ? "null" : "ok")} ");

        int ward;
        int division;
        var plot = 0;
        var apartment = 0;
        bool isApartment;
        var outdoorTerritory = currentTerritoryId;

        if (indoor)
        {
            if (!TryReadIndoor(h, currentTerritoryId, out ward, out division, out plot, out apartment, out isApartment, out outdoorTerritory, diag))
                return false;
        }
        else
        {
            var wardIndex = h->GetCurrentWard();
            if (wardIndex < 0)
            {
                diag?.Append($"outdoor ward={wardIndex} fail");
                return false;
            }

            division = h->GetCurrentDivision();
            if (division is not (1 or 2))
            {
                diag?.Append($"outdoor div={division} fail");
                return false;
            }

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

            diag?.Append($"outdoor w{ward} div{division} plot={plot} apt={apartment} ");
        }

        var city = TryGetCityForTerritory(outdoorTerritory)
                   ?? TryGetCityForTerritory(currentTerritoryId)
                   ?? (territoryRow is { } row ? TryGetCityByPlaceNameRegion(row.PlaceNameRegion.RowId) : null);

        if ((city is null or 0) && sheet is not null)
        {
            if (outdoorTerritory != 0
                && sheet.TryGetRow(outdoorTerritory, out var outdoorRow))
            {
                city = TryGetCityByPlaceNameRegion(outdoorRow.PlaceNameRegion.RowId)
                       ?? TryGetCityForTerritory(outdoorTerritory);
            }

            if ((city is null or 0)
                && territoryRow is { } cur
                && TryGetCityByPlaceNameRegion(cur.PlaceNameRegion.RowId) is { } fromCurrent)
            {
                city = fromCurrent;
            }
        }

        if (city is null or 0)
        {
            diag?.Append($"city unresolved (outTerr={outdoorTerritory} region={territoryRow?.PlaceNameRegion.RowId})");
            return false;
        }

        outdoorTerritory = TryGetResidentialTerritoryForCity(city.Value) ?? outdoorTerritory;
        if (!IsResidentialTerritory(outdoorTerritory))
        {
            var mapped = TryGetResidentialTerritoryForCity(city.Value);
            if (mapped is null)
            {
                diag?.Append($"outdoor terr {outdoorTerritory} not residential");
                return false;
            }

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
            Indoor = indoor || h->IsInside(),
            OutdoorTerritoryId = outdoorTerritory,
        };

        if (!location.IsHousing)
        {
            diag?.Append($"IsHousing false (w{ward} city={city} div{division})");
            return false;
        }

        diag?.Append(
            $"ok indoor={location.Indoor} city={location.City} w{location.Ward} div{location.Division} "
            + $"plot={location.Plot} apt={location.Apartment} outTerr={location.OutdoorTerritoryId}");
        return true;
    }

    private static unsafe bool TryReadIndoor(
        HousingManager* h,
        uint currentTerritoryId,
        out int ward,
        out int division,
        out int plot,
        out int apartment,
        out bool isApartment,
        out uint outdoorTerritory,
        StringBuilder? diag)
    {
        ward = 0;
        division = 0;
        plot = 0;
        apartment = 0;
        isApartment = false;
        outdoorTerritory = currentTerritoryId;

        // Prefer packed HouseId on IndoorTerritory (ClientStructs: combines ward, plot, room).
        // GetCurrentWard/Plot are often -1 indoors; GetCurrentIndoorHouseId can also be empty.
        var houseId = default(HouseId);
        var source = "none";
        if (h->IndoorTerritory is not null)
        {
            houseId = h->IndoorTerritory->HouseId;
            if (houseId.Id != 0)
                source = "IndoorTerritory.HouseId";
        }

        if (houseId.Id == 0)
        {
            houseId = h->GetCurrentIndoorHouseId();
            if (houseId.Id != 0)
                source = "GetCurrentIndoorHouseId";
        }

        if (houseId.Id == 0)
        {
            houseId = h->GetCurrentHouseId();
            if (houseId.Id != 0)
                source = "GetCurrentHouseId";
        }

        var original = HousingManager.GetOriginalHouseTerritoryTypeId();
        var mgrWard = h->GetCurrentWard();
        var mgrPlot = h->GetCurrentPlot();
        var mgrDiv = h->GetCurrentDivision();
        var mgrRoom = h->GetCurrentRoom();

        diag?.Append(
            $"houseSrc={source} id=0x{houseId.Id:X} wardIdx={houseId.WardIndex} plotIdx={houseId.PlotIndex} "
            + $"room={houseId.RoomNumber} apt={houseId.IsApartment} aptDiv={houseId.ApartmentDivision} "
            + $"houseTerr={houseId.TerritoryTypeId} houseWorld={houseId.WorldId} "
            + $"origTerr={original} mgrWard={mgrWard} mgrPlot={mgrPlot} mgrDiv={mgrDiv} mgrRoom={mgrRoom} ");

        if (original != 0)
            outdoorTerritory = original;
        else if (houseId.TerritoryTypeId != 0)
            outdoorTerritory = houseId.TerritoryTypeId;

        if (houseId.Id == 0)
        {
            // Last resort: manager APIs alone (usually fail indoors).
            if (mgrWard < 0)
            {
                diag?.Append("no HouseId and mgrWard<0 ");
                return false;
            }

            ward = mgrWard + 1;
            division = mgrDiv is 1 or 2 ? mgrDiv : 1;
            isApartment = mgrPlot is -128 or -127;
            if (mgrPlot == -127)
                division = 2;
            else if (mgrPlot == -128)
                division = 1;
            else if (mgrPlot >= 0)
                plot = mgrPlot + 1;
            if (isApartment && mgrRoom > 0)
                apartment = mgrRoom;
            return ward > 0 && division is 1 or 2;
        }

        // HouseId present — prefer it for identity.
        ward = houseId.WardIndex + 1;
        isApartment = houseId.IsApartment || mgrPlot is -128 or -127;

        if (isApartment)
        {
            if (mgrPlot == -127)
                division = 2;
            else if (mgrPlot == -128)
                division = 1;
            else
                division = houseId.ApartmentDivision + 1; // 0/1 → main/sub

            if (division is not (1 or 2))
            {
                diag?.Append($"apt div invalid={division} ");
                return false;
            }

            if (mgrRoom > 0)
                apartment = mgrRoom;
            else if (houseId.RoomNumber is > 0 and < 0x3FF)
                apartment = houseId.RoomNumber;
        }
        else
        {
            // Prefer HouseId.PlotIndex: indoors GetCurrentPlot is often -1, and when present
            // it can lack division context. Lifestream house plots are 1–60 (31–60 = subdivision).
            var plotIndex = houseId.PlotIndex;
            plot = plotIndex + 1;
            division = plotIndex >= 30 ? 2 : 1;

            if (division is not (1 or 2) || plot is < 1 or > 60)
            {
                diag?.Append($"house plot/div invalid plot={plot} div={division} ");
                return false;
            }
        }

        if (ward <= 0)
        {
            diag?.Append("ward<=0 ");
            return false;
        }

        return true;
    }
}
