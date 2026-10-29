using System.Numerics;
using Sade.Contracts;
using Sade.Mount;
using Sade.Reader;

static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
var shoulder = new Vector3(0, 0, 50);
var elbow = new Vector3(2, 3, 25);
var wrist = new Vector3(4, 5, 47);
var upper = Vector3.Distance(shoulder, elbow); var lower = Vector3.Distance(elbow, wrist);
foreach (var target in new[] { new Vector3(100, 0, 50), new(0, 100, 70), new(-100, 0, 40), new(0, 0, 120), new(0, 0, -20), new(.001f, 0, 50) })
{
    Check(ArmAimSolver.Solve(shoulder, elbow, wrist, target, elbow - shoulder, out var e, out var w), "Valid target rejected");
    Check(Math.Abs(Vector3.Distance(shoulder, e) - upper) < .001f, "Upper arm length changed");
    Check(Math.Abs(Vector3.Distance(e, w) - lower) < .001f, "Forearm length changed");
    Check(Vector3.Dot(Vector3.Normalize(w - shoulder), Vector3.Normalize(target - shoulder)) > .9999f, "Arm missed target direction");
    var q = ArmAimSolver.AimMuzzle(Quaternion.CreateFromAxisAngle(Vector3.UnitY, -MathF.PI / 2), w, new Vector3(10, 2, 4), target + Vector3.Normalize(target - shoulder) * 300);
    var muzzle = w + Vector3.Transform(new Vector3(10, 2, 4), q);
    Check(Vector3.Dot(Vector3.Transform(Vector3.UnitX, q), Vector3.Normalize(target + Vector3.Normalize(target - shoulder) * 300 - muzzle)) > .9999f, "Muzzle did not align");
}
Check(!ArmAimSolver.Solve(shoulder, shoulder, wrist, Vector3.One, Vector3.UnitZ, out _, out _), "Degenerate upper arm accepted");
Check(!ArmAimSolver.Solve(shoulder, elbow, wrist, new(float.NaN, 0, 0), Vector3.UnitZ, out _, out _), "Nonfinite target accepted");
Check(Vector3.Dot(Vector3.Transform(Vector3.UnitX, ArmAimSolver.Between(Vector3.UnitX, -Vector3.UnitX)), -Vector3.UnitX) > .9999f, "Antiparallel aim failed");
Console.WriteLine("PASS: two-bone length preservation, unreachable/near/vertical targets, muzzle alignment and degenerate inputs.");

if (args.Contains("--installed"))
{
    using var reader = new GameReader();
    reader.Initialize(new(Environment.GetEnvironmentVariable("SADE_GAME_DIR") ?? @"C:\Program Files (x86)\Steam\steamapps\common\GTA San Andreas - The Definitive Edition", Path.Combine(Path.GetTempPath(), "sade-reader-smoke")));
    foreach (var modelName in new[] { "SK_smoke", "SK_ballas1" })
    foreach (var weaponId in new[] { "colt45", "ak47" })
    {
        var model = reader.LoadModel("/Game/SanAndreas/Characters/Peds/" + modelName);
        var weapon = reader.GetWeapons().Single(w => w.Id == weaponId);
        Check(weapon.AimWithArm == (weaponId == "colt45"), "Source bAimWithArm flag mapping is incorrect");
        var mesh = reader.LoadModel(weapon.ModelId);
        var clip = reader.LoadAnimations(weapon.AnimationBankId, model, true, [weapon.FireClip]).Single();
        var frame = clip.Frames[Math.Clamp((int)MathF.Round(weapon.FireLoopStart * clip.FrameRate), 0, clip.Frames.Length - 1)];
        var world = new TransformData[frame.Length];
        for (var i = 0; i < frame.Length; i++)
        {
            var local = frame[i]; var parent = model.Bones[i].Parent;
            world[i] = parent < 0 ? local : new TransformData(world[parent].Position + Vector3.Transform(local.Position * world[parent].Scale, world[parent].Rotation), Quaternion.Normalize(world[parent].Rotation * local.Rotation), world[parent].Scale * local.Scale);
        }
        TransformData Bone(string name) => world[Array.FindIndex(model.Bones, b => b.Name == name)];
        var s = Bone("R_UpperArm"); var e = Bone("R_ForeArm"); var w = Bone("R_Hand");
        var target = new Vector3(700, 0, s.Position.Z);
        var goal = s.Position + Vector3.Normalize(target - s.Position) * (Vector3.Distance(s.Position, e.Position) + Vector3.Distance(e.Position, w.Position)) * .96f;
        Check(ArmAimSolver.Solve(s.Position, e.Position, w.Position, goal, e.Position - s.Position, out var e2, out var w2), "Real source arm solve failed");
        var q = ArmAimSolver.AimMuzzle(w.Rotation, w2, weapon.MuzzleOffset, target);
        var origin = w2 + Vector3.Transform(weapon.MuzzleOffset, q);
        var dot = Vector3.Dot(Vector3.Transform(Vector3.UnitX, q), Vector3.Normalize(target - origin));
        Check(dot > .9999f, "Real source pistol muzzle missed");
        Check(Math.Abs(Vector3.Distance(e2, w2) - Vector3.Distance(e.Position, w.Position)) < .001f, "Real forearm stretched");
        var pivot = Bone("Spine1").Position;
        var torso = ArmAimSolver.AimTorso(pivot, w.Position, w.Rotation, weapon.MuzzleOffset, target);
        var torsoWrist = pivot + Vector3.Transform(w.Position - pivot, torso);
        var torsoRotation = Quaternion.Normalize(torso * w.Rotation);
        var torsoMuzzle = torsoWrist + Vector3.Transform(weapon.MuzzleOffset, torsoRotation);
        var torsoDot = Vector3.Dot(Vector3.Transform(Vector3.UnitX, torsoRotation), Vector3.Normalize(target - torsoMuzzle));
        Check(torsoDot > .9999f, "Real torso aim missed");
        var leftHand = Bone("L_Hand").Position;
        var newLeft = pivot + Vector3.Transform(leftHand - pivot, torso);
        Check(Math.Abs(Vector3.Distance(newLeft, torsoWrist) - Vector3.Distance(leftHand, w.Position)) < .001f, "Torso aiming broke two-hand grip");
        Console.WriteLine($"SOURCE {modelName}/{weaponId}: authored barrel={Vector3.Transform(Vector3.UnitX, w.Rotation)}, arm muzzle dot={dot}, torso dot={torsoDot}, mesh bounds={mesh.BoundsMin}..{mesh.BoundsMax}");
    }
    Console.WriteLine("PASS: installed Smoke/Ballas firearm poses and source colt45/AK47 geometry.");
}
