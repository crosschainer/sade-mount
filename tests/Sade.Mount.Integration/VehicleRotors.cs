// Real source skin weights and actual renderer bone transforms. Every authored helicopter rotor must keep its geometric span at rest and while spinning.
var assembly=AppDomain.CurrentDomain.GetAssemblies().Single(a=>a.GetName().Name=="sade");
var flags=System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance;
var nativeType=assembly.GetType("Sade.Mount.NativeVehicle")!;
var runtime=assembly.GetType("Sade.Mount.VehicleRuntime")!;
var profiles=runtime.GetField("profiles",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static)!.GetValue(null)!;
var mount=Sandbox.Mounting.Directory.Get("sade");
var scene=new Scene();var results=new System.Collections.Generic.List<object>();
object Read(object o,string p)=>o.GetType().GetProperty(p)!.GetValue(o)!;
using(scene.Push())
try
{
    var camera=scene.CreateObject().Components.Create<CameraComponent>();camera.FieldOfView=55;camera.BackgroundColor=new Color(.16f,.20f,.24f);
    scene.CreateObject().Components.Create<AmbientLight>().Color=new Color(.65f,.65f,.65f);
    var light=scene.CreateObject().Components.Create<PointLight>();light.LightColor=Color.White*4;light.Radius=3000;light.Shadows=false;
    foreach(var name in new[]{"leviathn","hunter","seaspar","sparrow","maverick","vcnmav","polmav","cargobob","raindanc"})
    {
        var path=$"mount://sade/vehicles/helicopter/{name}.prefab";PrefabFile.Load(path);var vehicle=GameObject.Clone(path);
        var renderer=vehicle.GetComponent<SkinnedModelRenderer>();var body=vehicle.GetComponent<Rigidbody>();
        var profile=profiles.GetType().GetProperty("Item")!.GetValue(profiles,[renderer.Model.Name])!;
        var data=Read(profile,"Data");var model=mount.GetType().GetMethod("GetModelData",flags)!.Invoke(mount,[Read(data,"ModelId")])!;
        var bones=(Array)Read(model,"Bones");
        var vertices=((Array)Read(model,"Parts")).Cast<object>().Where(p=>(int)Read(p,"Lod")==0).SelectMany(p=>((Array)Read(p,"Vertices")).Cast<object>()).ToArray();
        var create=vehicle.Components.GetType().GetMethods().Single(m=>m.Name=="Create"&&m.IsGenericMethodDefinition);
        var native=(Component)create.MakeGenericMethod(nativeType).Invoke(vehicle.Components,[false])!;
        nativeType.GetMethod("Initialize",flags)!.Invoke(native,[profile,body,renderer]);body.MotionEnabled=false;
        GameObject? temporaryDriver=null;
        scene.GameTick(.02);renderer.SceneModel.Update(0);
        var stages=new System.Collections.Generic.List<object>(); var originalSpans=new System.Collections.Generic.Dictionary<string,float>(); var originalRotations=new System.Collections.Generic.Dictionary<string,Rotation>();
        foreach(var stage in new[]{"before","rest","spinning"})
        {
            if(stage!="before")
            {
                if(stage=="spinning"&&name=="maverick")
                {
                    temporaryDriver=scene.CreateObject();var visual=new GameObject(temporaryDriver,true,"Temporary pilot renderer");
                    var human=visual.Components.Create<SkinnedModelRenderer>();human.Model=Model.Load("models/citizen_human/human.vmdl");
                    var driver=temporaryDriver.Components.Create<PlayerController>();driver.Renderer=human;driver.UseCameraControls=false;driver.UseInputControls=false;
                    driver.Components.Create<Sandbox.Movement.SitMoveMode>();for(var frame=0;frame<3;frame++)scene.GameTick(.02);
                    vehicle.GetComponentsInChildren<BaseChair>().Single(c=>c.GameObject.Tags.Has("sade_vehicle_driver",false)).Sit(driver);
                    for(var frame=0;frame<150;frame++){scene.GameTick(.02);nativeType.GetMethod("Tick",flags)!.Invoke(native,[.02f]);}
                    if(!ReferenceEquals(nativeType.GetProperty("Driver",flags)!.GetValue(native),driver)||(float)nativeType.GetField("rotorSpeed",flags)!.GetValue(native)!<.99f)
                        throw new Exception("The occupied Maverick did not spool its rotor through the real seat and Tick path.");
                }
                else
                {
                    nativeType.GetField("rotorSpeed",flags)!.SetValue(native,stage=="rest"?0f:1f);
                    nativeType.GetMethod("UpdateRotors",flags)!.Invoke(native,[.02f]);
                }
                renderer.SceneModel.Update(0);
            }
            var rotorResults=new System.Collections.Generic.List<object>();
            for(var i=0;i<bones.Length;i++)
            {
                var sourceBone=bones.GetValue(i)!;var boneName=(string)Read(sourceBone,"Name");if(!boneName.Contains("rotor"))continue;
                renderer.TryGetBoneTransform(boneName,out var pose);
                var points=new System.Collections.Generic.List<Vector3>();
                foreach(var vertex in vertices)
                {
                    var indices=Read(vertex,"Bones");var weights=(System.Numerics.Vector4)Read(vertex,"Weights");
                    if(!((int)Read(indices,"X")==i&&weights.X>.5f||(int)Read(indices,"Y")==i&&weights.Y>.5f||(int)Read(indices,"Z")==i&&weights.Z>.5f||(int)Read(indices,"W")==i&&weights.W>.5f))continue;
                    var p=(System.Numerics.Vector3)Read(vertex,"Position");var position=new Vector3(p.X,-p.Y,p.Z)/2.54f;
                    var bind=renderer.Model.GetBoneTransform(i);points.Add(pose.PointToWorld(bind.PointToLocal(position)));
                }
                var min=points.Count==0?Vector3.Zero:points.Aggregate(new Vector3(float.MaxValue),(a,p)=>Vector3.Min(a,p));
                var max=points.Count==0?Vector3.Zero:points.Aggregate(new Vector3(float.MinValue),(a,p)=>Vector3.Max(a,p));
                if(points.Count>0)
                {
                    if(stage=="before"){originalSpans[boneName]=(max-min).Length;originalRotations[boneName]=pose.Rotation;}
                    else
                    {
                        if(Math.Min(pose.Scale.x,Math.Min(pose.Scale.y,pose.Scale.z))<.99f||(max-min).Length<originalSpans[boneName]*.65f)
                            throw new Exception($"{name}/{boneName}: authored rotor geometry collapsed at {stage} ({points.Count} vertices).");
                        if(stage=="spinning"&&Math.Max(Vector3.GetAngle(originalRotations[boneName].Forward,pose.Rotation.Forward),Vector3.GetAngle(originalRotations[boneName].Up,pose.Rotation.Up))<10)
                            throw new Exception($"{name}/{boneName}: weighted rotor geometry did not spin.");
                    }
                }
                rotorResults.Add(new{boneName,Vertices=points.Count,Scale=pose.Scale,Span=max-min,pose.Position,Rotation=pose.Rotation.Angles()});
            }
            string? image=null;
            if(name is "maverick" or "hunter" && stage is "rest" or "spinning")
            {
                var center=renderer.Model.Bounds.Center;var size=renderer.Model.Bounds.Size.Length;
                camera.WorldPosition=center+new Vector3(size*.6f,-size*.7f,size*.5f);
                camera.WorldRotation=Rotation.LookAt(center-camera.WorldPosition);light.WorldPosition=camera.WorldPosition;
                using var bitmap=new Bitmap(1200,900);camera.RenderToBitmap(bitmap,false);
                image=System.IO.Path.Combine(exchangeDirectory,$"rotors-{name}-{stage}.png");System.IO.File.WriteAllBytes(image,bitmap.ToPng());
            }
            stages.Add(new{stage,Rotors=rotorResults,image});
        }
        results.Add(new{name,Stages=stages});nativeType.GetMethod("Shutdown",flags)!.Invoke(native,null);vehicle.Destroy();temporaryDriver?.Destroy();scene.GameTick(.02);
    }
}
finally{scene.Destroy();}
return new{Passed=results.Count,Scope="Actual source-weighted rotor geometry in an isolated scene; Maverick uses an engine chair and real Tick to spool, other helicopters use deterministic rotor state.",Vehicles=results};
