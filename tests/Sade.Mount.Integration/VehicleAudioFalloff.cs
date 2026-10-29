// Actual engine attenuation parameters, with an isolated listener at three distances.
// No active player, camera, map, or mixer settings are changed.
var assembly=AppDomain.CurrentDomain.GetAssemblies().Single(a=>a.GetName().Name=="sade");
var audioType=assembly.GetType("Sade.Mount.NativeVehicleAudio")!;
var flags=System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance;
var scene=new Scene();var results=new System.Collections.Generic.List<object>();
var disposables=new System.Collections.Generic.List<IDisposable>();
try
{
    foreach(var (id,family) in new[]{(411,"car"),(487,"helicopter")})
    {
        object audio;SoundHandle handle;SoundHandle baseline;AudioListener listener;
        using(scene.Push())
        {
            audio=Activator.CreateInstance(audioType,flags,null,[id,family,5,10f],null)!;disposables.Add((IDisposable)audio);
            for(var i=0;i<60;i++)audioType.GetMethod("Update",flags)!.Invoke(audio,[Vector3.Zero,true,.3f,.6f,1f,.02f,true]);
            handle=(SoundHandle)audioType.GetField("engineHandle",flags)!.GetValue(audio)!;
            var file=(SoundFile)audioType.GetField("engine",flags)!.GetValue(audio)!;
            baseline=Sound.PlayFile(file);baseline.Volume=0;disposables.Add(baseline);
            listener=scene.CreateObject().Components.Create<AudioListener>();listener.UseCameraDirection=false;
        }
        var volume=handle.Volume;var distances=new System.Collections.Generic.List<object>();
        foreach(var distance in new[]{50f,300f,1000f})
        {
            for(var i=0;i<6;i++)
            {
                using(scene.Push()){listener.WorldPosition=new Vector3(distance,0,0);scene.GameTick(.05);}
                await Task.Delay(50,cancellationToken);
            }
            using(scene.Push())
            {
                var nativeListener=listener.GetType().GetField("_listener",flags)!.GetValue(listener)!;
                var direct=handle.GetType().GetMethod("GetDirectSoundModel",flags)!.Invoke(handle,[nativeListener])!;
                var parameters=direct.GetType().GetMethod("GetParams",flags)!.Invoke(direct,null)!;
                var curve=(Curve)parameters.GetType().GetField("Falloff",flags)!.GetValue(parameters)!;
                var range=(float)parameters.GetType().GetField("Distance",flags)!.GetValue(parameters)!;
                var gain=curve.Evaluate(distance/range);var original=baseline.Falloff.Evaluate(distance/range);
                if(distance==50&&Math.Abs(gain-original)>.002f)throw new Exception($"{family}: cabin gain changed ({original}->{gain}).");
                if(distance>=300&&(gain<original*1.5f||gain<.1f))throw new Exception($"{family}: exterior falloff is still too steep at {distance}: {gain}.");
                if(handle.Volume!=volume||!handle.IsPlaying)throw new Exception($"{family}: distance change altered source volume or stopped playback.");
                distances.Add(new{distance,Gain=gain,OriginalGain=original,Range=range,Volume=handle.Volume,Playing=handle.IsPlaying});
            }
        }
        results.Add(new{id,family,Distances=distances});
        using(scene.Push()){((IDisposable)audio).Dispose();baseline.Stop();listener.GameObject.Destroy();}
    }
}
finally{using(scene.Push()){foreach(var disposable in disposables)disposable.Dispose();scene.Destroy();}}
return new{Passed=results.Count,Results=results,Scope="Real direct-sound attenuation curves at fixed listener distances, unchanged source gain; perceptual listening remains separate."};
