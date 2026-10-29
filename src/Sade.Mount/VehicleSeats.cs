using Sandbox;
using Sandbox.Movement;
using Sade.Contracts;

namespace Sade.Mount;

/// <summary>
/// Seats remain ordinary serialized engine chairs, including their network RPCs and sitting animation.
/// The mount adds local discovery and controls around those chairs without a project component dependency.
/// </summary>
internal static class VehicleSeats
{
    private const string DriverTag = "sade_vehicle_driver";
    private const string PassengerTag = "sade_vehicle_passenger";
    private const string AccessTag = "sade_vehicle_access";
    private const string ExitTag = "sade_vehicle_exit";
    private const string SelectedExitTag = "sade_vehicle_selected_exit";
    private const float Reach = 90;
    private static readonly Dictionary<NativeVehicle, Seat[]> vehicles = [];
    private static readonly Dictionary<Scene, SeatControls> scenes = [];

    internal static void BuildPrefab(GameObject root, VehicleData data)
    {
        if (data.RemoteControlled || data.Family is "train" or "trailer") return;
        var front = data.Sockets.FirstOrDefault(s => s.Name.Equals("ped_frontseat", StringComparison.OrdinalIgnoreCase));
        if (front is null) return; // Do not invent seats in an asset without a human seating socket.
        var renderer = root.GetComponentInChildren<SkinnedModelRenderer>();
        var bounds = renderer?.Model?.Bounds ?? new BBox(new Vector3(-40, -80, -30), new Vector3(40, 80, 50));
        var riding = data.Family is "bike" or "bicycle";
        var frontPosition = CoordinateSpace.Position(front.Transform.Position);
        var paired = !riding && Math.Abs(frontPosition.x) > 5;
        AddSeat(frontPosition.WithX(paired ? -Math.Abs(frontPosition.x) : frontPosition.x), true, "front", -1);
        if (paired) AddSeat(frontPosition.WithX(Math.Abs(frontPosition.x)), false, "front", 1);
        var rear = data.Sockets.FirstOrDefault(s => s.Name.Equals("ped_backseat", StringComparison.OrdinalIgnoreCase) ||
            s.Name.Equals("ped_rearseat", StringComparison.OrdinalIgnoreCase));
        if (rear is not null)
        {
            var p = CoordinateSpace.Position(rear.Transform.Position);
            if (!riding && Math.Abs(p.x) > 5)
            {
                AddSeat(p.WithX(-Math.Abs(p.x)), false, "rear", -1);
                AddSeat(p.WithX(Math.Abs(p.x)), false, "rear", 1);
            }
            else AddSeat(p, false, "rear", 1);
        }

        void AddSeat(Vector3 pelvis, bool driver, string row, int side)
        {
            // GTA's socket anchors the pelvis. Citizen's seated graph retains a root below the hips.
            // This adapter offset is independent of the original vehicle socket coordinates.
            var seatObject = new GameObject(root, true, driver ? "Driver seat" : "Passenger seat");
            seatObject.Tags.Add(driver ? DriverTag : PassengerTag);
            seatObject.LocalPosition = pelvis - Vector3.Up * 22;
            seatObject.LocalRotation = Rotation.FromYaw(90); // GTA vehicles face local +Y.
            var chair = seatObject.Components.Create<BaseChair>();
            chair.SeatPosition = seatObject;
            chair.SitPose = riding ? BaseChair.AnimatorSitPose.ChairForward : BaseChair.AnimatorSitPose.Chair;
            chair.TooltipTitle = ""; chair.TooltipIcon = "";
            chair.PitchRange = new Vector2(-70, 65);
            chair.YawRange = new Vector2(-110, 110);
            var eyes = new GameObject(seatObject, true, "Seated eyes") { LocalPosition = Vector3.Up * 50 };
            chair.EyePosition = eyes;
            var doorName = "door_" + (side < 0 ? "l" : "r") + (row == "front" ? "f" : "r");
            var door = data.Sockets.FirstOrDefault(s => s.Name.Equals(doorName, StringComparison.OrdinalIgnoreCase));
            var doorPoint = door is null ? pelvis : CoordinateSpace.Position(door.Transform.Position);
            var sideWidth = Math.Clamp(Math.Max(Math.Abs(bounds.Mins.x), Math.Abs(bounds.Maxs.x)), 18, 100);
            var outsideX = side * Math.Max(Math.Abs(doorPoint.x) + 30, sideWidth + 25);
            var access = new GameObject(root, true, driver ? "Driver door" : "Passenger door");
            access.Tags.Add(AccessTag);
            access.LocalPosition = new Vector3(outsideX, doorPoint.y, bounds.Mins.z + 8);
            // The tag ties each chair to its serialized access/exit anchors after cloning and loading.
            var key = "sade_seat_" + (driver ? "driver" : row + (side < 0 ? "_left" : "_right"));
            access.Tags.Add(key); access.Tags.Add(ExitTag); seatObject.Tags.Add(key);
            var opposite = new GameObject(root, true, "Alternate seat exit")
            {
                LocalPosition = access.LocalPosition.WithX(-outsideX)
            };
            var behind = new GameObject(root, true, "Rear seat exit")
            {
                LocalPosition = new Vector3(outsideX, bounds.Mins.y - 35, bounds.Mins.z + 8)
            };
            opposite.Tags.Add(key); opposite.Tags.Add(ExitTag);
            behind.Tags.Add(key); behind.Tags.Add(ExitTag);
            var selected = new GameObject(seatObject, true, "Clear exit position");
            selected.Tags.Add(SelectedExitTag);
            chair.ExitPoints = [access, opposite, behind];
        }
    }

    internal static void Attach(NativeVehicle vehicle)
    {
        if (vehicles.ContainsKey(vehicle)) return;
        var seats = vehicle.GetComponentsInChildren<BaseChair>()
            .Where(c => c.GameObject.Tags.Has(DriverTag, false) || c.GameObject.Tags.Has(PassengerTag, false))
            .Select(c =>
            {
                var key = c.GameObject.Tags.TryGetAll(false).First(t => t.StartsWith("sade_seat_", StringComparison.Ordinal));
                var exits = vehicle.GameObject.Children.Where(e => e.Tags.Has(ExitTag, false) && e.Tags.Has(key, false))
                    .OrderByDescending(e => e.Tags.Has(AccessTag, false)).ToArray();
                var selectedExit = c.GameObject.Children.FirstOrDefault(e => e.Tags.Has(SelectedExitTag, false));
                if (selectedExit is null)
                {
                    selectedExit = new GameObject(c.GameObject, true, "Clear exit position");
                    selectedExit.Tags.Add(SelectedExitTag);
                }
                return new Seat(c, c.GameObject.Tags.Has(DriverTag, false), exits, selectedExit);
            })
            .ToArray();
        vehicles.Add(vehicle, seats);
        if (!scenes.ContainsKey(vehicle.Scene)) scenes.Add(vehicle.Scene, new SeatControls(vehicle.Scene));
    }

    internal static void Tick(NativeVehicle vehicle)
    {
        if (!vehicles.TryGetValue(vehicle, out var seats)) return;
        // Remote players also evaluate their animator locally. They need the engine sit mode even
        // though the mode itself is deliberately not a network component owned by this mount.
        foreach (var seat in seats)
        {
            var occupant = seat.Chair.IsValid ? seat.Chair.GetOccupant() : null;
            if (occupant.IsValid()) EnsureEngineSitMode(occupant);
        }
        var driver = seats.FirstOrDefault(s => s.Driver)?.Chair.GetOccupant();
        if (!ReferenceEquals(driver, vehicle.Driver))
        {
            vehicle.Driver = driver.IsValid() ? driver : null;
            if (Networking.IsHost && vehicle.GameObject.Network.Active)
            {
                if (driver.IsValid() && driver.Network.Owner is { } owner)
                    vehicle.GameObject.Network.AssignOwnership(owner);
                else vehicle.GameObject.Network.TakeOwnership();
            }
        }
        if (scenes.TryGetValue(vehicle.Scene, out var hud)) hud.Tick();
    }

    private static SitMoveMode? EnsureEngineSitMode(PlayerController player)
    {
        if (player.GetComponent<SitMoveMode>().IsValid()) return null;
        var mode = player.Components.Create<SitMoveMode>();
        mode.Flags = ComponentFlags.Hidden | ComponentFlags.NotSaved | ComponentFlags.NotCloned | ComponentFlags.NotNetworked;
        return mode;
    }

    internal static bool EnterSeat(BaseChair chair, PlayerController player)
    {
        var press = new Component.IPressable.Event(player);
        if (!chair.CanPress(press)) return false;
        EnsureEngineSitMode(player);
        // Offline editor players have no Network.Owner, while Rpc.Caller is still Local.
        // The engine's host RPC owner check therefore rejects them; use its same sit implementation locally.
        if (!Networking.IsActive && !player.IsProxy) { chair.Sit(player); return true; }
        var entered = chair.Press(press);
        if (entered) ((Component.IPressable)chair).Release(press);
        return entered;
    }

    private static void LeaveSeat(BaseChair chair, PlayerController player)
    {
        if (!Networking.IsActive && !player.IsProxy && chair.GetOccupant() == player) chair.Eject(player);
        else chair.AskToLeave(player);
    }

    internal static void Detach(NativeVehicle vehicle)
    {
        if (!vehicles.Remove(vehicle, out var seats)) return;
        foreach (var seat in seats)
        {
            var occupant = seat.Chair.IsValid ? seat.Chair.GetOccupant() : null;
            if (seat.Chair.IsValid) seat.Chair.ExitPoints = seat.Exits;
            if (vehicle.Scene.IsDestroyed) continue;
            if (occupant is not { IsValid: true, IsProxy: false }) continue;
            // Teardown must not delete the local player along with the vehicle hierarchy.
            var exit = TryExit(vehicle, seat, occupant, out var clear) ? clear : occupant.WorldPosition + Vector3.Up * 40;
            occupant.GameObject.SetParent(null, true);
            occupant.WorldPosition = exit;
            if (occupant.Body.IsValid()) occupant.Body.Enabled = true;
            if (occupant.ColliderObject.IsValid()) occupant.ColliderObject.Enabled = true;
        }
        if (vehicles.Keys.Any(v => v.Scene == vehicle.Scene)) return;
        if (scenes.Remove(vehicle.Scene, out var hud)) hud.Dispose();
    }

    private static bool TryExit(NativeVehicle vehicle, Seat seat, PlayerController player, out Vector3 result)
    {
        var radius = Math.Clamp(player.BodyRadius, 8, 32);
        var height = Math.Max(player.BodyHeight, radius * 2 + 1);
        var capsule = new Capsule(Vector3.Up * (radius + 2), Vector3.Up * (height - radius), radius);
        foreach (var anchor in seat.Exits.Where(e => e.IsValid()))
        {
            var point = anchor.WorldPosition;
            var ground = vehicle.Scene.Trace.Ray(point + Vector3.Up * 60, point - Vector3.Up * 180)
                .IgnoreGameObjectHierarchy(vehicle.GameObject).WithoutTags("trigger").Run();
            if (ground.Hit && !ground.StartedSolid && ground.Normal.z > .5f) point = ground.EndPosition + Vector3.Up * 3;
            // Include the vehicle itself in the clearance check: wings and neighboring cars can obstruct a door.
            var clearance = vehicle.Scene.Trace.Capsule(capsule, point, point + Vector3.Up)
                .IgnoreGameObjectHierarchy(player.GameObject).WithoutTags("trigger").Run();
            if (clearance.Hit || clearance.StartedSolid) continue;
            result = point;
            return true;
        }
        result = default;
        return false;
    }

    private sealed record Seat(BaseChair Chair, bool Driver, GameObject[] Exits, GameObject? SelectedExit)
    {
        internal GameObject? Door => Exits.FirstOrDefault();
    }

    private sealed class SeatControls(Scene scene) : IDisposable
    {
        private PlayerController? localPlayer, capturedPlayer;
        private bool savedPressing;
        private double lastFrame = double.NaN, scanAt, pendingUntil;
        private BaseChair? pending;
        private SitMoveMode? addedMode;

        internal void Tick()
        {
            if (lastFrame == Time.NowDouble) return;
            lastFrame = Time.NowDouble;
            if (!scene.IsValid || scene.IsEditor || !Game.IsPlaying) { return; }
            if (!localPlayer.IsValid() || localPlayer.IsProxy || Time.NowDouble >= scanAt)
            {
                localPlayer = scene.GetAllComponents<PlayerController>().FirstOrDefault(p => p.IsValid && p.Active && !p.IsProxy);
                scanAt = Time.NowDouble + .5;
            }
            var player = localPlayer;
            if (player is not { IsValid: true }) { ReleasePlayer(); return; }
            var entries = vehicles.Where(v => v.Key.IsValid && v.Key.Active && v.Key.Scene == scene).ToArray();
            var occupied = entries.SelectMany(v => v.Value.Select(s => (Vehicle: v.Key, Seat: s)))
                .FirstOrDefault(x => x.Seat.Chair.IsValid && x.Seat.Chair.GetOccupant() == player);
            if (occupied.Seat is not null)
            {
                CapturePlayer(player);
                EnsureSitMode(player);
                pending = null;
                var canExit = TryExit(occupied.Vehicle, occupied.Seat, player, out var exit);
                if (Input.Keyboard.Pressed("e"))
                {
                    Input.Clear(player.UseButton);
                    if (canExit)
                    {
                        // BaseChair selects from these points on the owning client after its host-authorized RPC.
                        var anchor = occupied.Seat.SelectedExit;
                        if (anchor is not null) { anchor.WorldPosition = exit; occupied.Seat.Chair.ExitPoints = [anchor]; }
                        LeaveSeat(occupied.Seat.Chair, player);
                    }
                }
                return;
            }
            if (pending.IsValid() && Time.NowDouble < pendingUntil)
            {
                return;
            }
            pending = null;
            ReleasePlayer();
            // Players already seated in another system's chair should not jump between vehicles.
            if (player.GetComponentInParent<ISitTarget>() is not null) { return; }
            var near = entries.SelectMany(v => v.Value.Select(s => (Vehicle: v.Key, Seat: s)))
                .Where(x => x.Seat.Chair.IsValid && x.Seat.Door.IsValid())
                .Select(x => (x.Vehicle, x.Seat, Distance: (x.Seat.Door!.WorldPosition - player.WorldPosition).Length))
                .Where(x => x.Distance <= Reach)
                .OrderBy(x => x.Distance).ToArray();
            if (near.Length == 0) { return; }
            var nearest = near[0].Vehicle;
            var nearbySeats = near.Where(x => x.Vehicle == nearest).ToArray();
            var driverSeat = nearbySeats.FirstOrDefault(x => x.Seat.Driver && !x.Seat.Chair.IsOccupied).Seat;
            var passenger = nearbySeats.FirstOrDefault(x => !x.Seat.Driver && !x.Seat.Chair.IsOccupied).Seat;
            var chosen = Input.Keyboard.Pressed("e") ? driverSeat : Input.Keyboard.Pressed("g") ? passenger : null;
            if (chosen is null) return;
            Input.Clear(player.UseButton);
            var target = chosen.Door!.WorldPosition + Vector3.Up * 28;
            var approach = scene.Trace.Ray(player.EyeTransform.Position, target)
                .IgnoreGameObjectHierarchy(player.GameObject).IgnoreGameObjectHierarchy(nearest.GameObject)
                .WithoutTags("trigger").Run();
            if (approach.Hit || approach.StartedSolid) { return; }
            if (nearest.Speed > 220) { return; }
            EnsureSitMode(player);
            CapturePlayer(player);
            if (EnterSeat(chosen.Chair, player))
            {
                pending = chosen.Chair; pendingUntil = Time.NowDouble + 2;
            }
            else ReleasePlayer();
        }

        private void CapturePlayer(PlayerController player)
        {
            if (capturedPlayer != player)
            {
                ReleasePlayer(); capturedPlayer = player; savedPressing = player.EnablePressing;
            }
            // Suppress generic failed-use-to-leave until exit clearance has been checked.
            player.EnablePressing = false;
        }

        private void EnsureSitMode(PlayerController player)
        {
            addedMode = EnsureEngineSitMode(player) ?? addedMode;
        }

        private void ReleasePlayer()
        {
            if (capturedPlayer.IsValid()) capturedPlayer.EnablePressing = savedPressing;
            capturedPlayer = null;
        }

        public void Dispose()
        {
            ReleasePlayer();
            // Keep a currently active move mode until the engine selects walking on the following frame.
            // It is a standard engine component and is not serialized or sent over the network.
            if (addedMode.IsValid() && localPlayer?.Mode != addedMode) addedMode.Destroy();
        }
    }
}
