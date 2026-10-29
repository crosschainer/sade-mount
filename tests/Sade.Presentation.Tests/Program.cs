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
