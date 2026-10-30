using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Sade.Contracts;

namespace Sade.Mount;

internal sealed record SpawnPick(string ModelName, string Path, string SourceId);

/// <summary>
/// A small, deliberate spawn menu. These source identities were checked against the installed
/// San Andreas asset registry; selecting them never requires decoding every mesh in the game.
/// Raw resources remain available to the mount's dependency resolver.
/// </summary>
internal static class SpawnCatalog
{
    public static IReadOnlyList<SpawnPick> StaticAssets { get; } = Array.AsReadOnly(new[]
    {
        Static("LA/LAe/Buildings/SM_carlshou1_lae2", "Common buildings/CJ's house"),
        Static("LA/LAe/Buildings/SM_sweetshou1_lae2", "Common buildings/Sweet's house"),
        Static("LA/LAe/Buildings/SM_rydhou01_lae2", "Common buildings/Ryder's house"),
        Static("LA/LAe/Buildings/SM_compfukhouse3", "Common buildings/Detached house"),
        Static("LA/LAe/Buildings/SM_ganghous02_lax", "Common buildings/Suburban house"),
        Static("LA/LAe/Buildings/SM_cjsaveg", "Common buildings/CJ's garage"),
        Static("Country/CountryW/Buildings/SM_farmhouse01", "Common buildings/Farmhouse"),
        Static("Country/countryE/Buildings/SM_cornerstore_01", "Common buildings/Corner store"),
        Static("Country/countryE/Buildings/SM_sw_diner1", "Common buildings/Diner"),
        Static("Country/countryE/Buildings/SM_sw_genstore01", "Common buildings/General store"),
        Static("LA/LAe/Buildings/SM_cluckinbell1_lae", "Common buildings/Cluckin' Bell"),
        Static("LA/LAw/Buildings/SM_burger01_law", "Common buildings/Burger Shot"),

        Static("Props/SM_dyn_ramp", "Ramps/Portable ramp"),
        Static("Props/SM_ramp", "Ramps/Ramp 1"),
        Static("Props/SM_newramp", "Ramps/Ramp 2"),
        Static("Props/SM_kmb_ramp", "Ramps/Stunt ramp"),
        Static("Props/SM_kmb_jump1", "Ramps/Jump ramp 1"),
        Static("Props/SM_sf_jump", "Ramps/Jump ramp 2"),
        Static("Props/SM_d9_ramp", "Ramps/Loading ramp"),
        Static("INT/SM_kickramp03", "Ramps/Arena kicker 1"),
        Static("INT/SM_kickramp04", "Ramps/Arena kicker 2"),
        Static("INT/SM_kickramp05", "Ramps/Arena kicker 3"),
        Static("INT/SM_kickramp06", "Ramps/Arena kicker 4"),
        Static("INT/SM_logramps", "Ramps/Log ramp"),

        Static("Props/SM_bin1", "Street props/Litter bin"),
        Static("Props/SM_cj_dumpster", "Street props/Dumpster 1"),
        Static("Props/SM_cj_dumpster2", "Street props/Dumpster 2"),
        Static("Props/SM_cj_big_skip1", "Street props/Waste skip"),
        Static("Props/SM_parkbench1", "Street props/Park bench"),
        Static("Props/SM_stonebench1", "Street props/Stone bench"),
        Static("Props/SM_parktable1", "Street props/Picnic table"),
        Static("Props/SM_bustopm", "Street props/Bus shelter"),
        Static("Props/SM_phonebooth1", "Street props/Phone booth"),
        Static("Props/SM_cj_phone_kiosk", "Street props/Payphone kiosk"),
        Static("Props/SM_fire_hydrant", "Street props/Fire hydrant"),
        Static("Props/SM_ce_mailbox1", "Street props/Mailbox 1"),
        Static("Props/SM_ce_mailbox2", "Street props/Mailbox 2"),
        Static("Props/SM_lamppost1", "Street props/Street lamp 1"),
        Static("Props/SM_lamppost2", "Street props/Street lamp 2"),
        Static("Props/SM_lamppost3", "Street props/Street lamp 3"),
        Static("Props/SM_trafficlight1", "Street props/Traffic light"),
        Static("Props/SM_bollard", "Street props/Bollard"),
        Static("Props/SM_bollardlight", "Street props/Illuminated bollard"),
        Static("Props/SM_vendmach", "Street props/Vending machine"),
        Static("Props/SM_vendmachfd", "Street props/Snack machine"),
        Static("LA/LAs/SM_nt_aircon1dbl", "Street props/Air conditioner"),

        Static("Props/SM_trafficcone", "Construction props/Traffic cone"),
        Static("Props/SM_roadworkbarrier1", "Construction props/Roadwork barrier"),
        Static("Props/SM_police_barrier", "Construction props/Police barrier"),
        Static("Props/SM_cj_roadbarrier", "Construction props/Road barrier"),
        Static("Props/SM_cj_water_barrier", "Construction props/Water-filled barrier"),
        Static("Props/SM_dyn_roadbarrier_2", "Construction props/Barrier 2"),
        Static("Props/SM_dyn_roadbarrier_3", "Construction props/Barrier 3"),
        Static("Props/SM_dyn_roadbarrier_4", "Construction props/Barrier 4"),
        Static("Props/SM_blockpallet", "Construction props/Block pallet"),
        Static("Props/SM_dyn_crate_1", "Construction props/Wooden pallet"),
        Static("Props/SM_dyn_crate_2", "Construction props/Crate 2"),
        Static("Props/SM_dyn_crate_3", "Construction props/Crate 3"),
        Static("Props/SM_cj_metal_crate", "Construction props/Metal crate"),
        Static("Props/SM_barrel1", "Construction props/Barrel 1"),
        Static("Props/SM_barrel2", "Construction props/Barrel 2"),
        Static("Props/SM_barrel3", "Construction props/Barrel 3"),
        Static("Props/SM_barrel4", "Construction props/Barrel 4"),
        Static("Props/SM_fencesmallb", "Construction props/Short fence")
    });

    private sealed record PedPick(string SourceId, string Path, int Order);
    private static readonly Dictionary<string, PedPick> Pedestrians = CreatePedestrians();

    /// <summary>Only complete, supported pedestrians with a locomotion preset get a menu entry.</summary>
    public static string? GetPedestrianPath(AssetEntry entry, PedestrianPreset preset)
    {
        if (entry.Kind != AssetKind.SkeletalMesh || string.IsNullOrWhiteSpace(preset.BankId)) return null;
        return Pedestrians.TryGetValue(entry.Id, out var pick) ? pick.Path : null;
    }

    public static int GetPedestrianOrder(AssetEntry entry) =>
        Pedestrians.TryGetValue(entry.Id, out var pick) ? pick.Order : int.MaxValue;

    public static string? GetVehiclePath(VehicleData data)
    {
        if (data.RemoteControlled || string.IsNullOrWhiteSpace(data.Id) ||
            data.Id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_')) return null;
        var folder = data.Family.ToLowerInvariant() switch
        {
            "road" => "Cars", "bike" => "Bikes", "bicycle" => "Bicycles",
            "boat" or "hovercraft" => "Boats", "helicopter" => "Helicopters", "plane" => "Planes",
            _ => null
        };
        if (folder is null) return null;
        var label = VehicleNames.TryGetValue(data.Id, out var name) ? name :
            CultureInfo.InvariantCulture.TextInfo.ToTitleCase(data.Id.ToLowerInvariant().Replace('_', ' '));
        return $"Vehicles/{folder}/{label}";
    }

    private static SpawnPick Static(string relative, string path)
    {
        var name = relative[(relative.LastIndexOf('/') + 1)..];
        return new(name, path, $"/Game/SanAndreas/Environment/{relative}.{name}");
    }

    private static Dictionary<string, PedPick> CreatePedestrians()
    {
        var result = new Dictionary<string, PedPick>(StringComparer.OrdinalIgnoreCase);
        void Add(string model, string path, int order, string folder = "Peds")
        {
            var name = "SK_" + model;
            var id = $"/Game/SanAndreas/Characters/{folder}/{name}.{name}";
            result.Add(id, new(id, "NPCs/" + path, order));
        }

        Add("Player_Combined", "Characters/CJ", 0, "Player");
        Add("smoke", "Characters/Big Smoke", 1);
        Add("sweet", "Characters/Sweet", 2, "SpecialPeds");
        Add("ryder2", "Characters/Ryder", 3, "SpecialPeds");
        Add("cesar", "Characters/Cesar Vialpando", 4, "SpecialPeds");
        Add("wuzimu", "Characters/Woozie", 5, "SpecialPeds");

        void Gang(string stem, string faction)
        {
            for (var i = 1; i <= 3; i++) Add(stem + i, $"Gangs/{faction}/{faction} {i}", 100);
        }
        Gang("fam", "Grove Street");
        Gang("ballas", "Ballas");
        Gang("lsv", "Vagos");
        Gang("vla", "Aztecas");
        Gang("sfr", "Rifa");
        Gang("dnb", "Da Nang Boys");
        Add("triada", "Gangs/Triads/Triad 1", 100);
        Add("triadb", "Gangs/Triads/Triad 2", 100);
        Add("triboss", "Gangs/Triads/Triad boss", 100);
        Add("maffa", "Gangs/Mafia/Mafia 1", 100);
        Add("maffb", "Gangs/Mafia/Mafia 2", 100);
        Add("mafboss", "Gangs/Mafia/Mafia boss", 100);

        Add("lapd1", "Police/Los Santos officer", 200);
        Add("sfpd1", "Police/San Fierro officer", 200);
        Add("lvpd1", "Police/Las Venturas officer", 200);
        Add("csher", "Police/Sheriff", 200);
        Add("lapdm1", "Police/Motorcycle officer", 200);
        Add("swat", "Police/SWAT officer", 200);
        Add("fbi", "Police/FBI agent", 200);
        Add("army", "Services/Soldier", 200);
        Add("laemt1", "Services/Los Santos paramedic", 200);
        Add("sfemt1", "Services/San Fierro paramedic", 200);
        Add("lvemt1", "Services/Las Venturas paramedic", 200);
        Add("lafd1", "Services/Los Santos firefighter", 200);
        Add("sffd1", "Services/San Fierro firefighter", 200);
        Add("lvfd1", "Services/Las Venturas firefighter", 200);

        Add("male01", "Civilians/Man 1", 300);
        Add("wmyst", "Civilians/Man 2", 300);
        Add("bmyst", "Civilians/Man 3", 300);
        Add("hmyst", "Civilians/Man 4", 300);
        Add("omyst", "Civilians/Man 5", 300);
        Add("wfyst", "Civilians/Woman 1", 300);
        Add("bfyst", "Civilians/Woman 2", 300);
        Add("hfyst", "Civilians/Woman 3", 300);
        Add("ofyst", "Civilians/Woman 4", 300);
        Add("wmybu", "Civilians/Businessman 1", 300);
        Add("bmybu", "Civilians/Businessman 2", 300);
        Add("wfybu", "Civilians/Businesswoman 1", 300);
        Add("bfybu", "Civilians/Businesswoman 2", 300);
        Add("wmycon", "Civilians/Construction worker", 300);
        Add("wmymech", "Civilians/Mechanic", 300);
        Add("bikera", "Civilians/Biker 1", 300);
        Add("bikerb", "Civilians/Biker 2", 300);
        Add("wmyjg", "Civilians/Male jogger", 300);
        Add("wfyjg", "Civilians/Female jogger", 300);
        Add("wmybe", "Civilians/Male beachgoer", 300);
        Add("wfybe", "Civilians/Female beachgoer", 300);
        Add("cwmyfr", "Civilians/Farmer", 300);
        Add("wmybar", "Civilians/Bartender", 300);
        return result;
    }

    private static readonly Dictionary<string, string> VehicleNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["landstal"] = "Landstalker", ["linerun"] = "Linerunner", ["peren"] = "Perennial",
        ["firetruk"] = "Fire Truck", ["trash"] = "Trashmaster", ["ambulan"] = "Ambulance",
        ["leviathn"] = "Leviathan", ["esperant"] = "Esperanto", ["washing"] = "Washington",
        ["mrwhoop"] = "Mr Whoopee", ["bfinject"] = "BF Injection", ["monster"] = "Monster Truck",
        ["seaspar"] = "Seasparrow", ["pizzaboy"] = "Pizza Boy", ["pcj600"] = "PCJ-600",
        ["coastg"] = "Coastguard", ["zr350"] = "ZR-350", ["baggage"] = "Baggage Tug",
        ["dozer"] = "Bulldozer", ["vcnmav"] = "News Maverick", ["fbiranch"] = "FBI Rancher",
        ["greenwoo"] = "Greenwood", ["blistac"] = "Blista Compact", ["polmav"] = "Police Maverick",
        ["hotring"] = "Hotring Racer", ["hotrina"] = "Hotring Racer A", ["hotrinb"] = "Hotring Racer B",
        ["bloodra"] = "Bloodring Banger", ["rnchlure"] = "Rancher Lure", ["supergt"] = "Super GT",
        ["bike"] = "Cruiser Bicycle", ["mtbike"] = "Mountain Bike", ["cropdust"] = "Cropduster",
        ["stunt"] = "Stuntplane", ["petro"] = "Tanker", ["rdtrain"] = "Roadtrain",
        ["buccanee"] = "Buccaneer", ["fcr900"] = "FCR-900", ["nrg500"] = "NRG-500",
        ["copbike"] = "Police Motorcycle", ["cement"] = "Cement Truck", ["fbitruck"] = "FBI Truck",
        ["combine"] = "Combine Harvester", ["remingtn"] = "Remington", ["firela"] = "Ladder Fire Truck",
        ["utility"] = "Utility Van", ["monstera"] = "Monster Truck A", ["monsterb"] = "Monster Truck B",
        ["raindanc"] = "Raindance", ["duneride"] = "Dune", ["dft30"] = "DFT-30",
        ["bf400"] = "BF-400", ["newsvan"] = "News Van", ["androm"] = "Andromada",
        ["copcarla"] = "Police Car (Los Santos)", ["copcarsf"] = "Police Car (San Fierro)",
        ["copcarvg"] = "Police Car (Las Venturas)", ["copcarru"] = "Police Ranger",
        ["swatvan"] = "SWAT Van", ["glenshit"] = "Glendale (damaged)", ["sadlshit"] = "Sadler (damaged)",
        ["boxburg"] = "Boxville (burglary)", ["bmx"] = "BMX", ["at400"] = "AT-400",
        ["quad"] = "Quad Bike", ["vortex"] = "Vortex (hovercraft)", ["hotdog"] = "Hotdog Van",
        ["topfun"] = "Topfun Van"
    };
}
