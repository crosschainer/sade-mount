using System.Numerics;
using Sade.Contracts;
using Sade.Mount;
using Sade.Reader;
using Sade.Formats;

void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
WorldPlacementData Point(string id, float x, float y) => new(id, id, new(new(x, y, 0), Quaternion.Identity, Vector3.One));
var data = new WorldData("fixture", [Point("origin", 0, 0), Point("negative", -12801, -12801), Point("edge", 11_000, 0), Point("far", 2_000_000, 0)], []);
var spatial = new WorldSpatialIndex(data, 32);
var resident = new HashSet<int>();
var first = spatial.Query([Vector3.Zero], 64, resident, 1.2f);
Check(first.Any(p => p.Index == 0) && first.All(p => p.Index != 3), "Distant world cells must stay unloaded.");
// Exact model bounds replace the conservative unknown-model pivot margin.
foreach (var placement in data.Placements) spatial.SetBounds(new ModelData { Id = placement.ModelId, Parts = [], BoundsMin = new(-100), BoundsMax = new(100) });
Check(spatial.Query([Vector3.Zero], 100, resident, 1.2f).All(p => p.Index != 2), "An object beyond the load boundary must stay out.");
resident.Add(2);
Check(spatial.Query([Vector3.Zero], 100, resident, 1.2f).Any(p => p.Index == 2), "Resident objects must survive inside the unload hysteresis band.");
Check(spatial.Query([new Vector3(-12801, -12801, 0)], 64, resident, 1.2f).Any(p => p.Index == 1), "Negative grid coordinates must use floor, not truncation.");
Check(spatial.Query([Vector3.Zero, new Vector3(2_000_000, 0, 0)], 64, resident, 1.2f).Any(p => p.Index == 3), "Separated viewers must both receive nearby cells.");
Check(spatial.Query([new Vector3(float.NaN)], 64, resident, 1.2f).Count == 0, "Invalid viewers must not expand a query across the world.");
var oversized = new WorldSpatialIndex(new("fixture", [Point("wide", 0, 0)], []));
oversized.SetBounds(new ModelData { Id = "wide", Parts = [], BoundsMin = new(-100_000, -100, -10), BoundsMax = new(100_000, 100, 10) });
Check(oversized.Query([new Vector3(90_000, 0, 0)], 64, resident, 1.2f).Count == 1, "Large terrain must be selected by its extent rather than a distant pivot.");
var baked = new WorldSpatialIndex(new("fixture", [Point("terrain", 0, 0)], [])
{
    Bounds = [new("terrain", new(249_000, 168_000, 1000), new(250_000, 169_000, 2000))]
});
Check(baked.Query([new Vector3(249_500, 168_500, 1400)], 64, resident, 1.2f).Count == 1,
    "World-space terrain vertices must index in their actual city, not at the origin pivot.");
Console.WriteLine("PASS world spatial cells, negative coordinates, extent selection, hysteresis and multiple viewers.");

if (args.Contains("--installed"))
{
    var game = Environment.GetEnvironmentVariable("SADE_GAME_DIR") ?? throw new Exception("Set SADE_GAME_DIR for installed-world checks.");
    using var reader = new GameReader();
    var cache = Path.Combine(Path.GetTempPath(), "sade-world-audit");
    var catalog = reader.Initialize(new(game, cache));
    var before = System.Diagnostics.Stopwatch.StartNew();
    var world = reader.GetWorld();
    var cold = before.Elapsed;
    var ids = catalog.Where(p => p.Kind is AssetKind.StaticMesh or AssetKind.SkeletalMesh).Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
    Check(world.Placements.Length > 70_000 && world.FailedSections.Length == 0, "The installed exterior world must index without losing sections.");
    Check(world.Placements.All(p => ids.Contains(p.ModelId)), "Every world placement must refer to an indexed source model.");
    Check(world.BoundsComplete && world.Bounds.Length > 6000, "World bounds must be indexed before streaming or allocating textures.");
    var ground = world.Bounds.Single(b => b.ModelId.EndsWith("/SM_lae2_ground07.SM_lae2_ground07", StringComparison.OrdinalIgnoreCase));
    Check(ground.Min.X > 260_000 && ground.Max.Y > 190_000, "Origin-placed terrain must retain its baked world-space bounds.");
    var house = world.Placements.Single(p => p.ModelId.EndsWith("/SM_carlshou1_lae2.SM_carlshou1_lae2", StringComparison.OrdinalIgnoreCase));
    Check(Vector3.Distance(house.Transform.Position, new Vector3(249426.56f, 169621.1f, 1705.4688f)) < 1, "CJ's house must retain its cooked Unreal placement.");
    Check(world.Placements.Any(p => p.Prominent) && world.Placements.Any(p => p.Materials.Any(m => m is not null)), "Authored priority and instance materials must survive indexing.");
    foreach (var name in new[] { "stormdrainlae2_03", "lae2_ground07" })
    {
        var id = ids.Single(id => id.EndsWith($"/SM_{name}.SM_{name}", StringComparison.OrdinalIgnoreCase));
        var model = reader.LoadModel(id);
        Check(model.Collision.Source.StartsWith("Derived LOD0"), name + " must not reuse displaced local COL.");
        Check(model.Collision.Indices.Length == model.Parts.Where(p => p.Lod == 0).Sum(p => p.Indices.Length), "Baked collision must follow all LOD0 sections, preserving gaps.");
        var collisionMin = model.Collision.Vertices.Aggregate(Vector3.Min);
        var collisionMax = model.Collision.Vertices.Aggregate(Vector3.Max);
        Check(Vector3.Distance(collisionMin, model.BoundsMin) < 2 && Vector3.Distance(collisionMax, model.BoundsMax) < 2, name + " collision must occupy the visible mesh bounds.");
    }
    Console.WriteLine("PASS installed storm drain and ground collision alignment.");
    foreach (var name in new[] { "lae2_landhub05", "lae2_roads52", "stormdrainlae2_03" })
    {
        var id = ids.Single(id => id.EndsWith($"/SM_{name}.SM_{name}", StringComparison.OrdinalIgnoreCase));
        var model = reader.LoadWorldModel(id);
        Check(model.Collision.Surfaces.Length == model.Collision.Indices.Length / 3 && model.Collision.Surfaces.Length > 0, name + " must carry one surface group per detailed triangle.");
        Check(!model.Collision.Surfaces.Contains(SurfaceAudio.Unknown), name + " must resolve the installed surface audio IDs.");
        if (name == "lae2_landhub05") Check(model.Collision.Surfaces.Contains(SurfaceAudio.Grass) && model.Collision.Surfaces.Contains(SurfaceAudio.Concrete), "The grass and paving around Grove Street must stay distinct after scaled world-space transfer.");
    }
    Console.WriteLine("PASS installed surface groups on roads, grass and world-baked terrain.");
    foreach (var (name, mass) in new[] { ("blackbag2", 100f), ("cardboardbox", 10f) })
    {
        var prop = reader.LoadWorldModel($"/Game/SanAndreas/Environment/Props/SM_{name}.SM_{name}");
        Check(prop.WorldProp?.Mass == mass && prop.Collision.Indices.Length == 0 && prop.Collision.Hulls.Length + prop.Collision.Boxes.Length + prop.Collision.Spheres.Length > 0,
            name + " must retain its source mass and use convex movable collision.");
    }
    Check(reader.LoadWorldModel(house.ModelId).WorldProp is null && reader.LoadWorldModel(ground.ModelId).WorldProp is null, "Houses and terrain must stay fixed.");
    Console.WriteLine("PASS installed loose props and protected buildings/terrain.");
    var road = reader.LoadModel(ids.Single(id => id.EndsWith("/SM_lae2_roads52.SM_lae2_roads52", StringComparison.OrdinalIgnoreCase)));
    Check(road.Collision.Hulls.Length > 0, "The reported invisible wall must exercise the source road's convex collision.");
    var staticRoad = BakedMeshCollision.ForStaticWorld(road);
    Check(staticRoad.Collision.Hulls.Length == 0 && staticRoad.Collision.Indices.Length > 0 && road.Collision.Hulls.Length > 0,
        "Static roads need surface collision without replacing convex collision on spawnable props.");
    Check(ReferenceEquals(world, reader.GetWorld()), "Repeated world requests must reuse metadata.");
    reader.Initialize(new(game, cache)); before.Restart();
    Check(reader.GetWorld().Placements.Length == world.Placements.Length, "The disk placement cache must survive reader restart.");
    Console.WriteLine($"PASS {world.Placements.Length:N0} exterior placements, model references, CJ house transform, source materials and warm cache ({before.Elapsed.TotalMilliseconds:0}ms; first request {cold.TotalSeconds:0.0}s).");
}
