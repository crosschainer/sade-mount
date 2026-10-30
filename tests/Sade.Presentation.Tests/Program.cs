using System.Numerics;
using Sade.Contracts;
using Sade.Mount;

static void Check(bool value, string message) { if (!value) throw new Exception(message); }
static void Near(Vector3 a, Vector3 b) => Check(Vector3.Distance(a,b) < .001f, $"Expected {b}, got {a}");

var gait = new LocomotionPlayback();
for (var i=0; i<30; i++) gait.Update(45, 1f/60, 45, false);
Check(gait.Walking && !gait.Running, "Normal walk failed");
for (var i=0; i<240; i++)
{
    gait.Update(i % 2 == 0 ? 0 : 90, 1f/60, 45, false);
    Check(gait.Walking && !gait.Running, "A missing physics update restarted or changed the gait");
}
for (var i=0; i<40; i++) gait.Update(0, 1f/60, 45, false);
Check(!gait.Walking, "Stopping did not return to idle");
Console.WriteLine("PASS continuous walk across uneven movement updates, gait hysteresis, and actual stops.");

var bones = new[] { new BoneData("Root", -1, TransformData.Identity, TransformData.Identity),
    new BoneData("Bip01_L_Clavicle", 0, TransformData.Identity, TransformData.Identity),
    new BoneData("L_UpperArm", 1, TransformData.Identity, TransformData.Identity),
    new BoneData("R_UpperArm", 0, TransformData.Identity, TransformData.Identity) };
var raised = new TransformData(Vector3.One, Quaternion.CreateFromAxisAngle(Vector3.UnitX, 1), Vector3.One);
var firing = new AnimationClipData("sade_fire",30,[[raised,raised,raised,raised],[raised,raised,raised,raised]],false);
var idle = new AnimationClipData("idle",30,[[TransformData.Identity,TransformData.Identity,TransformData.Identity,TransformData.Identity]]);
var body = new ModelData { Id="body", Parts=[], Bones=bones, BoundsMin=-Vector3.One, BoundsMax=Vector3.One };
var layered = PedestrianAnimationLayers.RelaxFreeArm(firing,idle,body);
Check(layered.Frames.All(f=>f[1]==TransformData.Identity && f[2]==TransformData.Identity && f[0]==raised && f[3]==raised),"Free-arm mask changed firing arm or torso");
Check(firing.Frames[0][1]==raised,"Source clip was mutated");
Console.WriteLine("PASS one-handed layer preserves the idle free arm, firing arm, torso and source clip.");

var vertex = new VertexData(new(1,1,0),Vector3.UnitX,new(1,0,0,1),new(.25f,.5f),Vector4.One,new(0,0,0,0),new(1,0,0,0));
var wheel = new ModelData {Id="wheel",BoundsMin=new(-1,-2,-2),BoundsMax=new(1,2,2),Parts=[new("rim","rim_material",[vertex,vertex,vertex],[0,1,2])]};
body=body with{Parts=[new("body","paint",[vertex,vertex,vertex],[0,1,2]),new("body_lod","paint",[vertex,vertex,vertex],[0,1,2],1)]};
var car=new VehicleData("car","Car","body","road","CAR") {WheelRadiusFront=4,WheelRadiusRear=2, Sockets=[
    new("wheel_lf",TransformData.Identity with{Position=new(-10,20,0)}),
    new("wheel_rr",TransformData.Identity with{Position=new(10,-20,0)}),
    new("wheel_lf_dummy",TransformData.Identity)]};
var assembled=VehiclePreviewGeometry.Assemble(body,wheel,car);
Check(assembled.Parts.Length==6,"Expected two wheels at both body LODs; dummy socket must not add a wheel");
var left=assembled.Parts.First(p=>p.Name.StartsWith("wheel_lf/"));
var right=assembled.Parts.First(p=>p.Name.StartsWith("wheel_rr/"));
Near(left.Vertices[0].Position,new(-12,18,0)); Near(right.Vertices[0].Position,new(11,-19,0));
Near(left.Vertices[0].Normal,-Vector3.UnitX);
Check(left.MaterialId=="rim_material" && left.Vertices[0].UV==vertex.UV && left.Indices.SequenceEqual(new[]{0,1,2}),"Wheel material, UV or winding changed");
Check(assembled.BoundsMin.X<=-12 && assembled.BoundsMax.X>=11 && ReferenceEquals(assembled.Collision,body.Collision),"Preview bounds or original collision changed");
Check(body.Parts.Length==2 && wheel.Parts[0].Vertices[0]==vertex,"Cached source geometry was mutated");
Console.WriteLine("PASS wheel socket placement, front/rear size, side orientation, LODs, material/UV preservation and original collision.");

// Spawn selection must remain bounded and dependency-free: world chunks and internal resources
// are still resolvable, but never become menu entries just because their names match a keyword.
var picks = SpawnCatalog.StaticAssets;
Check(picks.Count == 64, "The deliberate static selection unexpectedly changed size");
var expectedCategories = new Dictionary<string,int> {
    ["Common buildings"] = 12, ["Ramps"] = 12, ["Street props"] = 22, ["Construction props"] = 18 };
foreach (var category in expectedCategories)
    Check(picks.Count(p => p.Path.StartsWith(category.Key + "/", StringComparison.Ordinal)) == category.Value,
        "Unexpected selection for " + category.Key);
Check(picks.Select(p => p.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() == picks.Count,
    "Two static selections have the same menu path");
Check(picks.Select(p => p.SourceId).Distinct(StringComparer.OrdinalIgnoreCase).Count() == picks.Count,
    "One static model is exposed more than once");
foreach (var pick in picks)
{
    Check(pick.SourceId.StartsWith("/Game/SanAndreas/Environment/", StringComparison.Ordinal) &&
        pick.SourceId.EndsWith("/" + pick.ModelName + "." + pick.ModelName, StringComparison.Ordinal),
        "Static selection must identify an exact San Andreas package and object");
    Check(!pick.SourceId.Contains("/Collision/", StringComparison.OrdinalIgnoreCase) &&
        !pick.SourceId.Contains("/Cutscene/", StringComparison.OrdinalIgnoreCase), "Internal geometry leaked into the menu");
    Check(!pick.Path.Split('/').Any(s => string.IsNullOrWhiteSpace(s) || s is "." or ".." ||
        s.IndexOfAny(['\\', ':', '*', '?', '"', '<', '>', '|']) >= 0), "Unsafe friendly resource path");
}
Check(picks.Single(p => p.Path == "Common buildings/CJ's house").ModelName == "SM_carlshou1_lae2" &&
    picks.Single(p => p.Path == "Common buildings/CJ's garage").ModelName == "SM_cjsaveg", "CJ house and garage were confused");
Check(picks.Single(p => p.ModelName == "SM_vendmach").SourceId ==
    "/Game/SanAndreas/Environment/Props/SM_vendmach.SM_vendmach", "Ambiguous GTA3 vending machine selected");

static AssetEntry Ped(string model, string folder = "Peds")
{
    var name = "SK_" + model;
    return new($"/Game/SanAndreas/Characters/{folder}/{name}.{name}", "unused", AssetKind.SkeletalMesh, name);
}
var preset = new PedestrianPreset("Person", "ifp:ped", "IDLE_stance", "WALK_player", "run_player", "man");
Check(SpawnCatalog.GetPedestrianPath(Ped("Player_Combined", "Player"), preset) == "NPCs/Characters/CJ", "CJ missing");
Check(SpawnCatalog.GetPedestrianPath(Ped("smoke"), preset) == "NPCs/Characters/Big Smoke", "Big Smoke missing");
Check(SpawnCatalog.GetPedestrianPath(Ped("ryder2", "SpecialPeds"), preset) == "NPCs/Characters/Ryder", "Ryder missing");
Check(SpawnCatalog.GetPedestrianPath(Ped("ballas1"), preset) == "NPCs/Gangs/Ballas/Ballas 1" &&
    SpawnCatalog.GetPedestrianPath(Ped("fam3"), preset) == "NPCs/Gangs/Grove Street/Grove Street 3", "Gang labels must remain recognizable outside their folder");
Check(SpawnCatalog.GetPedestrianPath(Ped("lapd1"), preset) == "NPCs/Police/Los Santos officer" &&
    SpawnCatalog.GetPedestrianPath(Ped("wmymech"), preset) == "NPCs/Civilians/Mechanic", "Everyday NPC selection changed");
Check(SpawnCatalog.GetPedestrianOrder(Ped("Player_Combined", "Player")) < SpawnCatalog.GetPedestrianOrder(Ped("smoke")) &&
    SpawnCatalog.GetPedestrianOrder(Ped("smoke")) < SpawnCatalog.GetPedestrianOrder(Ped("fam1")), "Featured NPC ordering changed");
foreach (var hidden in new[] { Ped("torso", "Player"), Ped("jeans", "Player"), Ped("smokev"), Ped("ryder3", "SpecialPeds"),
    Ped("smoke", "Cutscene"), Ped("smoke", "Peds/Clothes"), Ped("unknown") })
    Check(SpawnCatalog.GetPedestrianPath(hidden, preset) is null, "Modular, alternate or cutscene NPC exposed: " + hidden.Id);
Check(SpawnCatalog.GetPedestrianPath(Ped("smoke") with { Kind = AssetKind.StaticMesh }, preset) is null &&
    SpawnCatalog.GetPedestrianPath(Ped("smoke"), preset with { BankId = "" }) is null, "NPC without an animation source exposed");
Check(SpawnCatalog.GetPedestrianPath(Ped("smoke") with { Id = Ped("smoke").Id.ToUpperInvariant() }, preset) ==
    "NPCs/Characters/Big Smoke", "Source identity lookup became case-sensitive");

static VehicleData Vehicle(string id, string family) => new(id, id, "source", family, id);
var vehicleExamples = new[] {
    ("infernus", "road", "Vehicles/Cars/Infernus"), ("nrg500", "bike", "Vehicles/Bikes/NRG-500"),
    ("bmx", "bicycle", "Vehicles/Bicycles/BMX"), ("squalo", "boat", "Vehicles/Boats/Squalo"),
    ("vortex", "hovercraft", "Vehicles/Boats/Vortex (hovercraft)"), ("maverick", "helicopter", "Vehicles/Helicopters/Maverick"),
    ("at400", "plane", "Vehicles/Planes/AT-400"), ("copcarla", "road", "Vehicles/Cars/Police Car (Los Santos)") };
foreach (var (id, family, path) in vehicleExamples)
    Check(SpawnCatalog.GetVehiclePath(Vehicle(id, family)) == path, "Vehicle category or name incorrect: " + id);
foreach (var unsupported in new[] { Vehicle("freight", "train"), Vehicle("artict1", "trailer"), Vehicle("unknown", "unknown"),
    Vehicle("rcbandit", "road") with { RemoteControlled = true }, Vehicle("../bad", "road") })
    Check(SpawnCatalog.GetVehiclePath(unsupported) is null, "Unsupported or unsafe vehicle exposed: " + unsupported.Id);
var variantPaths = new[] { "hotring", "hotrina", "hotrinb", "monster", "monstera", "monsterb", "glendale", "glenshit",
    "sadler", "sadlshit", "boxville", "boxburg", "copcarla", "copcarsf", "copcarvg", "copcarru" }
    .Select(id => SpawnCatalog.GetVehiclePath(Vehicle(id, "road"))).ToArray();
Check(variantPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count() == variantPaths.Length, "Vehicle variant names collide");
var priorCulture = System.Globalization.CultureInfo.CurrentCulture;
try
{
    System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("tr-TR");
    Check(SpawnCatalog.GetVehiclePath(Vehicle("INFERNUS", "ROAD")) == "Vehicles/Cars/Infernus",
        "Vehicle menu paths depend on the user's locale");
}
finally { System.Globalization.CultureInfo.CurrentCulture = priorCulture; }
Console.WriteLine("PASS curated exact source identities, unique categories, complete NPC filtering, vehicle families and stable friendly paths.");
