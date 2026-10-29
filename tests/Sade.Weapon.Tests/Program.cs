using System.Numerics;
using Sade.Mount;

// The numeric solver must actually intercept different elevations at the configured speed.
foreach (var target in new[] { new Vector3(400, 0, 0), new Vector3(300, -200, 90), new Vector3(-240, 190, -80) })
{
    var start = new Vector3(10, 15, 48);
    if (!WeaponMechanics.TryBallisticVelocity(start, target, 800, 600, out var velocity)) throw new Exception("Reachable target rejected.");
    var horizontal = new Vector2(target.X - start.X, target.Y - start.Y).Length();
    var flight = horizontal / new Vector2(velocity.X, velocity.Y).Length();
    var result = start + velocity * flight - Vector3.UnitZ * (300 * flight * flight);
    Near(Vector3.Distance(result, target), 0, 0.001f);
    Near(velocity.Length(), 800, 0.001f);
}
if (WeaponMechanics.TryBallisticVelocity(Vector3.Zero, new Vector3(10000, 0, 0), 100, 600, out _) ||
    WeaponMechanics.TryBallisticVelocity(Vector3.Zero, Vector3.One, float.NaN, 600, out _)) throw new Exception("Impossible trajectory accepted.");
Console.WriteLine("PASS: ballistic shots intercept targets above and below the muzzle; impossible inputs rejected.");

foreach (var forward in new[] { Vector3.UnitX, Vector3.UnitZ, -Vector3.UnitZ, Vector3.Normalize(new(1, 2, 3)) })
{
    var rays = WeaponMechanics.PelletDirections(forward, 15, 2.8624f, new Random(12345));
    if (rays.Length != 15 || rays.Distinct().Count() != 15) throw new Exception("A shotgun needs fifteen independent pellets.");
    foreach (var ray in rays)
    {
        Near(ray.Length(), 1, 0.00001f);
        var angle = MathF.Acos(Math.Clamp(Vector3.Dot(ray, forward), -1, 1)) * 180 / MathF.PI;
        if (angle > 2.8625f) throw new Exception("Pellet escaped its configured cone.");
    }
    var exact = WeaponMechanics.PelletDirections(forward, 1, 0, new Random(1))[0];
    Near(Vector3.Distance(exact, forward), 0, 0.00001f);
}
Console.WriteLine("PASS: shotgun pellet count, dispersion, normalized directions and vertical aim.");

Near(WeaponMechanics.BlastFalloff(0, 100), 1);
Near(WeaponMechanics.BlastFalloff(50, 100), 0.5f);
Near(WeaponMechanics.BlastFalloff(100, 100), 0);
Near(WeaponMechanics.BlastFalloff(1000, 100), 0);
Near(WeaponMechanics.BlastFalloff(0, 0), 0);
Near(WeaponMechanics.BlastFalloff(float.NaN, 100), 0);
Console.WriteLine("PASS: blast damage attenuates and stays bounded at the radius boundary.");

if (!WeaponMechanics.AreRivals("Vagos", ["Aztecas"], "Aztecas", []) ||
    !WeaponMechanics.AreRivals("Aztecas", [], "Vagos", ["Aztecas"]) ||
    WeaponMechanics.AreRivals("Vagos", ["Aztecas"], "VAGOS", ["Vagos"]) ||
    WeaponMechanics.AreRivals("Vagos", ["Aztecas"], "Civilian", []) ||
    WeaponMechanics.AreRivals("Ballas", ["Vagos"], "Grove", []) ||
    WeaponMechanics.AreRivals("Vagos", ["Aztecas"], "Player", [])) throw new Exception("Gang source relationships or player neutrality changed.");
Console.WriteLine("PASS: asymmetric source hostility, same-gang friendship, neutral civilians/player, metadata precedence.");
Console.WriteLine("4 weapon regression groups passed.");

static void Near(float actual, float expected, float tolerance = 0.0001f)
{
    if (!float.IsFinite(actual) || Math.Abs(actual - expected) > tolerance) throw new Exception($"Expected {expected}, got {actual}.");
}
