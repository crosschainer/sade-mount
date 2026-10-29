using System.Text.Json.Nodes;
using Sade.Mount;

var first = MakeVehicle();
var nextLaunch = MakeVehicle();
StablePrefabIds.Apply(first, "mount://sade/vehicles/car/savanna.prefab");
StablePrefabIds.Apply(nextLaunch, @"MOUNT:\\sade\vehicles\car\savanna.prefab");
Check(first.ToJsonString() == nextLaunch.ToJsonString(), "Fresh random builder IDs must produce identical saved prefab data across launches and normalized paths.");
var saved = JsonNode.Parse(first.ToJsonString())!.AsObject();
StablePrefabIds.Apply(first, "mount://sade/vehicles/car/savanna.prefab");
Check(first.ToJsonString() == saved.ToJsonString(), "Stable identity assignment must be idempotent.");

var rootId = saved["__guid"]!.GetValue<Guid>();
var seat = saved["Children"]![0]!;
var chair = seat["Components"]![0]!;
var eyeId = seat["Children"]![0]!["__guid"]!.GetValue<Guid>();
Check(chair["SeatPosition"]!["go"]!.GetValue<Guid>() == seat["__guid"]!.GetValue<Guid>(), "Chair's self-reference must remain valid.");
Check(chair["EyePosition"]!["go"]!.GetValue<Guid>() == eyeId, "Nested chair eye reference must remain valid.");
Check(chair["Renderer"]!["component"]!.GetValue<Guid>() == saved["Components"]![0]!["__guid"]!.GetValue<Guid>(), "Component reference must remain valid.");
Check(chair["ExternalId"]!.GetValue<Guid>() == External, "External resource identities must remain unchanged.");

// A saved scene's prefab patch uses these target IDs. They must resolve in a fresh build.
var overrides = new[] { (Id: rootId, Property: "Position", Value: "120,340,56"), (Id: eyeId, Property: "Name", Value: "Saved camera") };
foreach (var patch in overrides)
{
    var target = Objects(nextLaunch).SingleOrDefault(x => x["__guid"]!.GetValue<Guid>() == patch.Id);
    Check(target is not null, "Saved prefab override target must survive a rebuild.");
    target![patch.Property] = patch.Value;
}
Check(nextLaunch["Position"]!.GetValue<string>() == "120,340,56", "Saved placement must apply to the rebuilt prefab root.");
Check(nextLaunch["Children"]![0]!["Children"]![0]!["Name"]!.GetValue<string>() == "Saved camera", "Saved nested override must apply after rebuild.");

var extended = MakeVehicle();
extended["Children"]!.AsArray().Insert(0, new JsonObject { ["__guid"] = Guid.NewGuid(), ["Name"] = "New unrelated helper", ["Tags"] = "" });
extended["Components"]!.AsArray().Insert(0, new JsonObject { ["__guid"] = Guid.NewGuid(), ["__type"] = "Sandbox.PointLight" });
StablePrefabIds.Apply(extended, "mount://sade/vehicles/car/savanna.prefab");
Check(extended["Children"]![1]!["__guid"]!.GetValue<Guid>() == seat["__guid"]!.GetValue<Guid>(), "Adding an unrelated sibling must not change existing seat identities.");
Check(extended["Components"]![1]!["__guid"]!.GetValue<Guid>() == saved["Components"]![0]!["__guid"]!.GetValue<Guid>(), "Adding another component type must not change existing component identities.");
var another = MakeVehicle();
StablePrefabIds.Apply(another, "mount://sade/vehicles/car/infernus.prefab");
Check(another["__guid"]!.GetValue<Guid>() != rootId, "Different prefab resources must have distinct roots.");
var duplicate = MakeVehicle();
duplicate["Children"]![0]!["__guid"] = duplicate["__guid"]!.DeepClone();
try { StablePrefabIds.Apply(duplicate, "mount://sade/bad.prefab"); throw new Exception("Duplicate identities were accepted."); }
catch (InvalidDataException) { }
Console.WriteLine("PASS: stable prefab identities, scene patch targets, nested seat/component references, additions, path isolation and duplicate rejection.");

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static IEnumerable<JsonObject> Objects(JsonObject root)
{
    yield return root;
    if (root["Children"] is not JsonArray children) yield break;
    foreach (var child in children.OfType<JsonObject>())
        foreach (var item in Objects(child)) yield return item;
}
static JsonObject MakeVehicle()
{
    var root = Guid.NewGuid(); var renderer = Guid.NewGuid(); var seat = Guid.NewGuid(); var eye = Guid.NewGuid();
    return new JsonObject
    {
        ["__guid"] = root, ["Name"] = "savanna", ["Position"] = "0,0,0",
        ["Components"] = new JsonArray(new JsonObject { ["__guid"] = renderer, ["__type"] = "Sandbox.SkinnedModelRenderer" }),
        ["Children"] = new JsonArray(new JsonObject
        {
            ["__guid"] = seat, ["Name"] = "Driver seat", ["Tags"] = "sade_vehicle_driver,sade_seat_driver",
            ["Components"] = new JsonArray(new JsonObject
            {
                ["__guid"] = Guid.NewGuid(), ["__type"] = "Sandbox.BaseChair",
                ["SeatPosition"] = new JsonObject { ["go"] = seat },
                ["EyePosition"] = new JsonObject { ["go"] = eye },
                ["Renderer"] = new JsonObject { ["go"] = root, ["component"] = renderer }, ["ExternalId"] = External
            }),
            ["Children"] = new JsonArray(new JsonObject { ["__guid"] = eye, ["Name"] = "Seated eyes", ["Tags"] = "" })
        })
    };
}

partial class Program
{
    private static readonly Guid External = Guid.Parse("11223344-5566-7788-99aa-bbccddeeff00");
}
