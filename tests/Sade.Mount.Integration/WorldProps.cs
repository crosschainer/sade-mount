// Real map bodies and Sandbox physgun selection. Moves one loose prop temporarily;
// restores its pose afterward, without moving the player or camera.
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
var scene = Game.ActiveScene;
var prop = scene.GetAllComponents<Rigidbody>().FirstOrDefault(b => b.GameObject.Tags.Has("sade_world_prop", false) && b.MotionEnabled)
    ?? throw new Exception("Wait for loose map props to load near Grove Street.");
var original = prop.WorldTransform;
var originalMotion = prop.MotionEnabled;
var obj = prop.GameObject;
Check(obj.Root == obj && prop.StartAsleep && prop.MassOverride > 0 && Math.Abs(prop.Mass - prop.MassOverride) < .01f, "A loose map prop needs an independent root, authored mass and sleeping startup.");
Check(scene.GetAllComponents<ModelCollider>().Any(c => c.GameObject.Name == "SM_carlshou1_lae2" && c.GetComponent<Rigidbody>() is null), "CJ's house must remain static.");
Check(scene.GetAllComponents<ModelCollider>().Any(c => c.GameObject.Name == "SM_lae2_roads52" && c.GetComponent<Rigidbody>() is null), "Roads must remain static.");
var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
var statics = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
var mount = Sandbox.Mounting.Directory.Get("sade");
var runtime = mount.GetType().Assembly.GetType("Sade.Mount.WorldRuntime")!;
var session = runtime.GetField("session", statics)!.GetValue(null)!;
object Field(object value, string name) => value.GetType().GetField(name, flags)!.GetValue(value)!;
var states = ((System.Collections.IEnumerable)Field(session, "states")).Cast<object>().Select(p => p.GetType().GetProperty("Value")!.GetValue(p)!).ToArray();
var state = states.Single(s => ((System.Collections.IEnumerable)Field(s, "instances")).Cast<object>().Any(p =>
    p.GetType().GetProperty("Value")!.GetValue(p)!.GetType().GetProperty("Object")!.GetValue(p.GetType().GetProperty("Value")!.GetValue(p)!) == obj));
var entry = ((System.Collections.IEnumerable)Field(state, "instances")).Cast<object>().Single(p =>
    p.GetType().GetProperty("Value")!.GetValue(p)!.GetType().GetProperty("Object")!.GetValue(p.GetType().GetProperty("Value")!.GetValue(p)!) == obj);
var id = (int)entry.GetType().GetProperty("Key")!.GetValue(entry)!;
var weaponType = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("Physgun", false)).FirstOrDefault(t => t is not null)
    ?? throw new Exception("Run in Sandbox to exercise its actual Physgun.");
var weaponObject = scene.CreateObject(); weaponObject.Name = "Temporary map-prop physgun probe";
GameObject? restored = null;
try
{
    var create = weaponObject.Components.GetType().GetMethods().Single(m => m.Name == "Create" && m.IsGenericMethodDefinition);
    var weapon = (Component)create.MakeGenericMethod(weaponType).Invoke(weaponObject.Components, [false])!;
    var model = obj.GetComponent<ModelRenderer>().Model;
    var center = obj.WorldTransform.PointToWorld(model.Bounds.Center);
    var aim = new Transform(center + Vector3.Up * 150, Rotation.LookAt(Vector3.Down));
    var find = weaponType.GetMethod("FindGrabbedBody", flags)!;
    var grabType = find.GetParameters()[0].ParameterType.GetElementType()!;
    object[] arguments = [Activator.CreateInstance(grabType)!, aim, false];
    Check((bool)find.Invoke(weapon, arguments)!, "Sandbox's actual physgun must select the loose map body.");
    var selectedBody = (Rigidbody?)grabType.GetProperty("Body")!.GetValue(arguments[0]);
    Check(selectedBody == prop, "Physgun selection must resolve this prop, not the map root.");
    Check((bool)weaponType.GetMethod("CanMove", flags)!.Invoke(weapon, [arguments[0]])!, "The selected body must pass normal physgun movement checks.");

    prop.WorldPosition += new Vector3(80, 0, 40); prop.MotionEnabled = false;
    var moved = prop.WorldTransform;
    state.GetType().GetMethod("CaptureProps", flags)!.Invoke(state, null);
    state.GetType().GetMethod("Remove", flags)!.Invoke(state, [id]);
    state.GetType().GetField("planAt", flags)!.SetValue(state, 0d);
    for (var i = 0; i < 200 && restored is null; i++)
    {
        await Task.Delay(50);
        var instances = (System.Collections.IDictionary)Field(state, "instances");
        if (instances.Contains(id)) restored = (GameObject)instances[id]!.GetType().GetProperty("Object")!.GetValue(instances[id])!;
    }
    Check(restored.IsValid(), "An unloaded moved prop must stream back in near its saved position.");
    var body = restored!.GetComponent<Rigidbody>();
    Check(body.IsValid() && !body.MotionEnabled && restored.WorldPosition.Distance(moved.Position) < .1f,
        "Streaming must retain the moved position and frozen state.");
    return new { Passed = true, Prop = restored.Name, Mass = body.MassOverride, NativePhysgunSelection = true, StaticHouseAndRoad = true, MovedAndFrozenPoseRetained = true };
}
finally
{
    if (restored.IsValid()) { restored!.WorldTransform = original; var body = restored.GetComponent<Rigidbody>(); body.MotionEnabled = originalMotion; body.Velocity = Vector3.Zero; body.Sleeping = true; }
    else if (obj.IsValid()) { obj.WorldTransform = original; prop.MotionEnabled = originalMotion; prop.Sleeping = true; }
    ((System.Collections.IDictionary)Field(state, "changedProps")).Remove(id);
    weaponObject.Destroy();
}
