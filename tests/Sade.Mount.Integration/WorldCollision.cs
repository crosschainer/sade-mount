// Run in the mounted Grove Street scene after nearby geometry has finished loading.
// This probe reads collision without moving the player or modifying the scene.
var scene = Game.ActiveScene;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
Check(scene.GetAllObjects(false).Any(o => o.Tags.Has("sade_world_grove", false)), "Open the Grove Street map first.");
var road = scene.GetAllComponents<ModelCollider>().FirstOrDefault(c => c.GameObject.Name == "SM_lae2_roads52" && c.Active);
Check(road.IsValid(), "Wait for road52 to load before running this probe.");
var start = new Vector3(-2040.832f, 1038.746f, -34.3f);
var wall = scene.Trace.Ray(start, start + Vector3.Backward * 400).WithTag("sade_world_geometry").Run();
Check(!wall.Hit, "The open street must not be blocked by road52's convex hull.");
var floor = scene.Trace.Ray(new Vector3(3957, 1436, 200), new Vector3(3957, 1436, -2000)).WithTag("sade_world_geometry").Run();
Check(floor.Hit && floor.GameObject?.Name == "SM_stormdrainlae2_03", "The reported fall-through point must hit the storm drain floor.");
var tested = 0;
foreach (var x in new float[] { -600, 0, 600, 1200, 1800, 2400, 3000, 3600, 3957, 4200, 4800 })
foreach (var y in new float[] { -1800, -1200, -600, 0, 600, 1200, 1436, 1800 })
{
    var a = new Vector3(x, y, 200); var b = a - Vector3.Up * 2200;
    var visible = scene.Trace.Ray(a, b).UseRenderMeshes().UsePhysicsWorld(false).WithTag("sade_world_geometry").Run();
    if (!visible.Hit) continue;
    var physical = scene.Trace.Ray(a, b).WithTag("sade_world_geometry").Run();
    Check(physical.Hit, $"Missing ground collision at {x}, {y} ({visible.GameObject?.Name}).");
    tested++;
}
Check(tested >= 80, "The neighbourhood must be loaded before checking its ground.");
return new { Passed = true, GroundSamples = tested, Floor = floor.EndPosition.ToString(), StreetClear = true };
