// Trusted run_csharp integration script. Requires the current mount and an offline editor.
// Exercises real engine chair/movement behavior in an isolated scene, without changing the active camera.
if (Networking.IsActive) throw new Exception("Run the offline seat regression before connecting a network session.");
var assembly = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "sade");
var seating = assembly.GetType("Sade.Mount.VehicleSeats")!;
var runtime = assembly.GetType("Sade.Mount.VehicleRuntime")!;
var nativeType = assembly.GetType("Sade.Mount.NativeVehicle")!;
var members = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
var statics = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
var enter = seating.GetMethod("EnterSeat", statics) ?? throw new Exception("Install the current mount: offline EnterSeat helper is missing.");
var leave = seating.GetMethod("LeaveSeat", statics)!;
var profiles = runtime.GetField("profiles", statics)!.GetValue(null)!;
var activeScene = Game.ActiveScene;
var activeCamera = activeScene?.Camera;
var scene = new Scene();
Component? native = null;
using (scene.Push())
try
{
    var floor = scene.CreateObject();
    floor.WorldPosition = new Vector3(0, 0, -10);
    var floorCollider = floor.Components.Create<BoxCollider>();
    floorCollider.Scale = new Vector3(2000, 2000, 20);
    floorCollider.Static = true;
    var path = "mount://sade/vehicles/road/infernus.prefab";
    if (PrefabFile.Load(path) is null) throw new Exception("Mounted Infernus prefab is unavailable.");
    var car = GameObject.Clone(path);
    car.WorldPosition = new Vector3(0, 0, 28);
    var body = car.GetComponent<Rigidbody>();
    var renderer = car.GetComponent<SkinnedModelRenderer>();
    var profile = profiles.GetType().GetProperty("Item")!.GetValue(profiles, [renderer.Model.Name])!;
    var create = car.Components.GetType().GetMethods().Single(m => m.Name == "Create" && m.IsGenericMethodDefinition);
    native = (Component)create.MakeGenericMethod(nativeType).Invoke(car.Components, [false])!;
    nativeType.GetMethod("Initialize", members)!.Invoke(native, [profile, body, renderer]);
    body.MotionEnabled = false;
    var driverSeat = car.GetComponentsInChildren<BaseChair>().Single(c => c.GameObject.Tags.Has("sade_vehicle_driver", false));
    var passengerSeat = car.GetComponentsInChildren<BaseChair>().First(c => c.GameObject.Tags.Has("sade_vehicle_passenger", false));
    var driver = CreatePlayer("Seat regression driver", new Vector3(-140, 0, 3));
    var passenger = CreatePlayer("Seat regression passenger", new Vector3(140, 0, 3));
    Step(3);
    if (!(bool)enter.Invoke(null, [driverSeat, driver])!) throw new Exception("Offline driver entry rejected.");
    Step(20);
    CheckSeated(driver, driverSeat);
    if ((bool)enter.Invoke(null, [driverSeat, passenger])!) throw new Exception("Occupied driver seat was stolen.");
    if (!(bool)enter.Invoke(null, [passengerSeat, passenger])!) throw new Exception("Offline passenger entry rejected.");
    Step(20);
    CheckSeated(passenger, passengerSeat);
    if (!ReferenceEquals(nativeType.GetProperty("Driver", members)!.GetValue(native), driver)) throw new Exception("Passenger replaced the driver.");
    var occupiedState = new { DriverMode = driver.Mode?.GetType().Name, PassengerMode = passenger.Mode?.GetType().Name, DriverBody = driver.Body.Enabled, PassengerBody = passenger.Body.Enabled };

    var seats = ((System.Collections.IDictionary)seating.GetField("vehicles", statics)!.GetValue(null)!)[native] as Array
        ?? throw new Exception("Native vehicle has no registered seats.");
    var exits = new System.Collections.Generic.List<object>();
    foreach (var player in new[] { passenger, driver })
    {
        var seat = seats.Cast<object>().Single(s => ((BaseChair)s.GetType().GetProperty("Chair", members)!.GetValue(s)!).GetOccupant() == player);
        var chair = (BaseChair)seat.GetType().GetProperty("Chair", members)!.GetValue(seat)!;
        var args = new object[] { native, seat, player, Vector3.Zero };
        if (!(bool)seating.GetMethod("TryExit", statics)!.Invoke(null, args)!) throw new Exception("Open test floor has no safe seat exit.");
        var point = (Vector3)args[3];
        var anchor = (GameObject?)seat.GetType().GetProperty("SelectedExit", members)!.GetValue(seat)
            ?? throw new Exception("Current prefab is missing its selected-exit anchor.");
        anchor.WorldPosition = point;
        chair.ExitPoints = [anchor];
        leave.Invoke(null, [chair, player]);
        Step(5);
        if (chair.IsOccupied || player.GetComponentInParent<Sandbox.Movement.ISitTarget>() is not null || player.Mode is Sandbox.Movement.SitMoveMode || !player.Body.Enabled || !player.ColliderObject.Enabled)
            throw new Exception($"{player.GameObject.Name}: exit did not restore walking: occupied={chair.IsOccupied}, parent={player.GameObject.Parent?.Name}, mode={player.Mode?.GetType().Name}, body={player.Body.Enabled}, collider={player.ColliderObject.Enabled}.");
        if (player.WorldPosition.Distance(point) > 8) throw new Exception("Exit did not place the player at the checked clear point.");
        exits.Add(new { Player = player.GameObject.Name, Mode = player.Mode?.GetType().Name, player.Body.Enabled, Position = player.WorldPosition, ClearPoint = point });
    }
    if (nativeType.GetProperty("Driver", members)!.GetValue(native) is not null) throw new Exception("Driver retained after leaving.");
    if (!driverSeat.CanPress(new Component.IPressable.Event(passenger))) throw new Exception("Vacated driver seat remains unavailable.");
    if (activeScene?.Camera != activeCamera) throw new Exception("Seat test changed the active scene camera.");
    return new { Passed = true, occupiedState, Exits = exits };

    PlayerController CreatePlayer(string name, Vector3 position)
    {
        var obj = scene.CreateObject(); obj.Name = name; obj.WorldPosition = position;
        var visual = new GameObject(obj, true, "Human renderer");
        var human = visual.Components.Create<SkinnedModelRenderer>();
        human.Model = Model.Load("models/citizen_human/human.vmdl");
        var player = obj.Components.Create<PlayerController>();
        player.Renderer = human; player.UseCameraControls = false; player.UseInputControls = false;
        return player;
    }
    void Step(int count)
    {
        for (var i = 0; i < count; i++)
        {
            scene.GameTick(.02);
            seating.GetMethod("Tick", statics)!.Invoke(null, [native]);
        }
    }
    void CheckSeated(PlayerController player, BaseChair chair)
    {
        if (chair.GetOccupant() != player || player.Mode is not Sandbox.Movement.SitMoveMode || player.Body.Enabled || player.ColliderObject.Enabled)
            throw new Exception($"{player.GameObject.Name}: seat pose/controller state is invalid.");
    }
}
finally
{
    if (native?.IsValid == true) nativeType.GetMethod("Shutdown", members)!.Invoke(native, null);
    scene.Destroy();
}
