These eight animation conversion/support files originate from FabianFG/CUE4Parse at commit `1e3293d4b3ecaf6ef3d73641b9ffe47fa4cf1562`, matching NuGet release `1.2.2.202610`.

Source: https://github.com/FabianFG/CUE4Parse/tree/1e3293d4b3ecaf6ef3d73641b9ffe47fa4cf1562/CUE4Parse-Conversion

- `AnimConverter.cs`: `Legacy/AnimConverter.cs`
- `CAnimSet.cs`, `CAnimSequence.cs`, `CAnimTrack.cs`, `FAnimationRuntime.cs`, `FCompactPose.cs`, `FPoseBone.cs`: `Writers/ActorX/Structs/Animations/`
- `Constants.cs`: `Constants.cs`

The source is Apache-2.0 licensed; the complete upstream license is in `LICENSE`. Files have source attribution headers and a nullable-warning-context directive added, with implementation unchanged. The narrow subset avoids unrelated glTF/image-export dependencies; meshes and audio are read directly through CUE4Parse core.
