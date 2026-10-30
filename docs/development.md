# Settings and development

## Settings

Create `mount/sade/sade.json` to override defaults, then restart s&box:

```json
{
  "GameDirectory": "D:\\Games\\GTA San Andreas - The Definitive Edition",
  "CacheDirectory": "D:\\Caches\\sade-mount",
  "RegisterAnimationVariants": true,
  "LodDistance": 2000,
  "WorldViewDistanceMeters": 450,
  "WorldCollisionDistanceMeters": 180,
  "WorldTextureMaxSize": 1024,
  "WorldFrameBudgetMilliseconds": 3,
  "WorldCacheMegabytes": 512
}
```

The default cache is `%LOCALAPPDATA%\sade-mount\cache`. Assets load on demand.
The world cache budget applies to unused resources; geometry still in view can
exceed it. Individual uploads and collision builds can exceed the frame budget.
Failures and surface-label fallbacks are reported by source asset in the console.

## Build

Install the .NET 10 SDK, then run from the repository in PowerShell:

```powershell
.\scripts\Build.ps1
.\scripts\Install.ps1 -SkipBuild
.\scripts\Release.ps1 -SkipBuild
```

The scripts find s&box through `FACEPUNCH_ENGINE` or Steam's default folder.
`-SboxDirectory` selects another installation; `-DotnetPath` selects a specific SDK.
The mount builds with assemblies from my installed version of s&box. Rebuild
after API changes. Output is `.artifacts/package/sade`, with the ZIP at
`.artifacts/sade-mount.zip`. Set `SADE_GAME_DIR` to include installed-game checks.

For the demo, run `.\scripts\Setup-Demo.ps1` and open
`Demo/sade_preview/sade_preview.sbproj`.

## Map implementation

`mount://sade/maps/San Andreas.scene` loads the exterior world at Grove Street.
Earlier map names remain hidden aliases. The editor's “Map is not compiled”
warning comes from a `.vpk` check; the mount supplies a SceneFile directly.

World bounds are indexed before conversion. Nearby geometry and textures load
on demand, with separate ownership from permanent spawn-menu resources. Detailed
static collision avoids convex hulls across streets. Some baked meshes need
collision derived from their visible triangles because retained COL coordinates
differ. Original face material IDs and `surfaud.dat` supply surface audio groups;
ambiguous matches use the original dominant group. Sand/gravel map to the stock
dirt surface, and long grass maps to grass.

Loose props require an authored `object.dat` entry, suitable mass and size, and
an allowed collision response. Their bodies use authored convex shapes where
available. Moved/frozen poses persist during streaming within the map session;
map-prop save/reload and multiplayer replication remain pending. See
[validation notes](validation.md) for test coverage and other limits.

## Source layout

`Sade.Mount` creates engine resources and runs behavior. `Sade.Reader` reads the
game in an isolated assembly context. `Sade.Contracts` carries data across that
boundary, and `Sade.Formats` parses classic formats. No game helper or engine
modification is required. Game assets and the decoder stay in the GTA installation.

Stable `mount://sade/` resource paths preserve saved references, including hidden
animation, loadout and legacy prefab paths. [Third-party notices](../THIRD-PARTY-NOTICES.md)
record dependencies and the Alan Wake mount reference.
