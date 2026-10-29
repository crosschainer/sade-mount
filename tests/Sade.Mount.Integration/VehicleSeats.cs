// Trusted run_csharp integration script. Requires the current mount and an offline editor.
// Exercises door-side selection and real engine chairs in an isolated scene, without changing the active camera.
// Selection uses the same helper as the E key; this fixture does not inject keyboard input.
var offline = !Networking.IsActive;
var assembly = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "sade");
var seating = assembly.GetType("Sade.Mount.VehicleSeats")!;
var runtime = assembly.GetType("Sade.Mount.VehicleRuntime")!;
var nativeType = assembly.GetType("Sade.Mount.NativeVehicle")!;
var members = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
var statics = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
var enter = seating.GetMethod("EnterSeat", statics) ?? throw new Exception("Install the current mount: offline EnterSeat helper is missing.");
var leave = seating.GetMethod("LeaveSeat", statics)!;
var findEntry = seating.GetMethod("FindEntry", statics) ?? throw new Exception("Install the current mount: door-based entry selection is missing.");
var profiles = runtime.GetField("profiles", statics)!.GetValue(null)!;
var activeScene = Game.ActiveScene;
var activeCamera = activeScene?.Camera;
var scene = new Scene();
Component? native = null;
Component? bikeNative = null;
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
    native.Enabled = true;
    body.MotionEnabled = false;
    var driverSeat = car.GetComponentsInChildren<BaseChair>().Single(c => c.GameObject.Tags.Has("sade_vehicle_driver", false));
    var passengerSeat = car.GetComponentsInChildren<BaseChair>().First(c => c.GameObject.Tags.Has("sade_vehicle_passenger", false));
    var driver = CreatePlayer("Seat regression driver", new Vector3(-140, 0, 3));
    var passenger = CreatePlayer("Seat regression passenger", new Vector3(140, 0, 3));
    Step(3);
    var driverDoor = Entrances(car, driverSeat).Single();
    var passengerDoor = Entrances(car, passengerSeat).Single();
    if (SelectAt(driverDoor.WorldPosition) != driverSeat || SelectAt(passengerDoor.WorldPosition) != passengerSeat)
        throw new Exception("Door proximity did not choose left driver / right passenger.");
    car.WorldRotation = Rotation.FromYaw(73);
    if (SelectAt(driverDoor.WorldPosition) != driverSeat || SelectAt(passengerDoor.WorldPosition) != passengerSeat)
        throw new Exception("Seat selection used world axes instead of the rotated vehicle's door sides.");
    if (SelectAt(car.WorldPosition + Vector3.Up * 1000) is not null)
        throw new Exception("Seat selection ignored the entry distance limit.");
    car.WorldRotation = Rotation.Identity;
    if (!Enter(driverSeat, driver)) throw new Exception("Driver entry rejected.");
    Step(20);
    CheckSeated(driver, driverSeat);
    var occupiedDriverChoice = SelectAt(driverDoor.WorldPosition);
    if (occupiedDriverChoice == driverSeat || occupiedDriverChoice is not null && occupiedDriverChoice.GameObject.LocalPosition.x > 1)
        throw new Exception("An occupied left door selected the occupied driver or an opposite-side passenger.");
    if (SelectAt(passengerDoor.WorldPosition) != passengerSeat)
        throw new Exception("An occupied driver seat prevented right-door passenger entry.");
    if (Enter(driverSeat, passenger)) throw new Exception("Occupied driver seat was stolen.");
    if (!Enter(passengerSeat, passenger)) throw new Exception("Passenger entry rejected.");
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
        if (offline) leave.Invoke(null, [chair, player]);
        else chair.Eject(player);
        Step(5);
        if (chair.IsOccupied || player.GetComponentInParent<Sandbox.Movement.ISitTarget>() is not null || player.Mode is Sandbox.Movement.SitMoveMode || !player.Body.Enabled || !player.ColliderObject.Enabled)
            throw new Exception($"{player.GameObject.Name}: exit did not restore walking: occupied={chair.IsOccupied}, parent={player.GameObject.Parent?.Name}, mode={player.Mode?.GetType().Name}, body={player.Body.Enabled}, collider={player.ColliderObject.Enabled}.");
        if (player.WorldPosition.Distance(point) > 8) throw new Exception("Exit did not place the player at the checked clear point.");
        exits.Add(new { Player = player.GameObject.Name, Mode = player.Mode?.GetType().Name, player.Body.Enabled, Position = player.WorldPosition, ClearPoint = point });
    }
    if (nativeType.GetProperty("Driver", members)!.GetValue(native) is not null) throw new Exception("Driver retained after leaving.");
    if (!driverSeat.CanPress(new Component.IPressable.Event(passenger))) throw new Exception("Vacated driver seat remains unavailable.");

    var bikePath = "mount://sade/vehicles/bike/nrg500.prefab";
    if (PrefabFile.Load(bikePath) is null) throw new Exception("Mounted NRG500 prefab is unavailable.");
    var bike = GameObject.Clone(bikePath);
    bike.WorldPosition = new Vector3(500, 0, 28);
    bike.WorldRotation = Rotation.FromYaw(-41);
    var bikeBody = bike.GetComponent<Rigidbody>();
    var bikeRenderer = bike.GetComponent<SkinnedModelRenderer>();
    var bikeProfile = profiles.GetType().GetProperty("Item")!.GetValue(profiles, [bikeRenderer.Model.Name])!;
    var bikeCreate = bike.Components.GetType().GetMethods().Single(m => m.Name == "Create" && m.IsGenericMethodDefinition);
    bikeNative = (Component)bikeCreate.MakeGenericMethod(nativeType).Invoke(bike.Components, [false])!;
    nativeType.GetMethod("Initialize", members)!.Invoke(bikeNative, [bikeProfile, bikeBody, bikeRenderer]);
    bikeNative.Enabled = true;
    bikeBody.MotionEnabled = false;
    var riderSeat = bike.GetComponentsInChildren<BaseChair>().Single(c => c.GameObject.Tags.Has("sade_vehicle_driver", false));
    var pillionSeat = bike.GetComponentsInChildren<BaseChair>().Single(c => c.GameObject.Tags.Has("sade_vehicle_passenger", false));
    foreach (var chair in new[] { riderSeat, pillionSeat })
    {
        var entrances = Entrances(bike, chair);
        if (entrances.Length != 2) throw new Exception("A bike saddle must be accessible from both sides.");
        foreach (var entrance in entrances)
            if (SelectAt(entrance.WorldPosition) != chair) throw new Exception("Bike front/rear proximity selected the wrong saddle.");
    }
    if (!Enter(riderSeat, driver)) throw new Exception("Bike rider entry rejected.");
    foreach (var entrance in Entrances(bike, riderSeat))
        if (SelectAt(entrance.WorldPosition) != pillionSeat) throw new Exception("An occupied bike rider seat did not leave the nearby pillion available.");
    if (activeScene?.Camera != activeCamera) throw new Exception("Seat test changed the active scene camera.");
    return new { Passed = true, occupiedState, Exits = exits, DoorSelection = "Left/right, rotated vehicle, occupied side, distance limit, and both-side bike rider/pillion selection.",
        Scope = offline ? "Offline native entry/exit and engine sitting." : "Isolated engine Sit/Eject and clearance; live network RPC/ownership is not tested." };

    bool Enter(BaseChair chair, PlayerController player)
    {
        if (offline) return (bool)enter.Invoke(null, [chair, player])!;
        // The active game can have networking without these disposable test players having
        // network owners. Exercise the actual engine chair locally without spawning any network object.
        if (!chair.CanPress(new Component.IPressable.Event(player))) return false;
        if (!player.GetComponent<Sandbox.Movement.SitMoveMode>().IsValid()) player.Components.Create<Sandbox.Movement.SitMoveMode>();
        chair.Sit(player);
        return true;
    }

    BaseChair? SelectAt(Vector3 point)
    {
        var entry = findEntry.Invoke(null, [scene, point]);
        if (entry is null) return null;
        var seat = entry.GetType().GetProperty("Seat", members)!.GetValue(entry)!;
        return (BaseChair)seat.GetType().GetProperty("Chair", members)!.GetValue(seat)!;
    }
    GameObject[] Entrances(GameObject vehicle, BaseChair chair)
    {
        var key = chair.GameObject.Tags.TryGetAll(false).Single(t => t.StartsWith("sade_seat_", StringComparison.Ordinal));
        return vehicle.Children.Where(c => c.Tags.Has("sade_vehicle_access", false) && c.Tags.Has(key, false)).ToArray();
    }

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
    if (bikeNative?.IsValid == true) nativeType.GetMethod("Shutdown", members)!.Invoke(bikeNative, null);
    if (native?.IsValid == true) nativeType.GetMethod("Shutdown", members)!.Invoke(native, null);
    scene.Destroy();
}
