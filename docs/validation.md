# Validation

Checked on Windows with s&box 26.10.27c, the Steam Definitive Edition build 16391096 and .NET 10. The mount reads the two installed PAK archives; extracted assets are kept out of the repository and release.

These notes cover v0.3, including the streamed map and the earlier NPC, vehicle and spawn-menu features.

The streamed map uses exterior-world placements. Installed-game checks
index 79,664 placements across 209 exterior sections with no failed sections,
resolve their model references, preserve CJ's house transform and instance materials,
and reuse the disk metadata cache. Spatial tests cover negative cells, multiple
viewers, unload hysteresis, large extents and meshes whose vertices are baked into
world coordinates. The storm drain and ground07 regressions reject local COL at
the origin; road52 retains convex collision as a prop but uses triangles in the map.

In the Grove Street preview, the reported road52 wall blocked a horizontal ray
11 units ahead while the render trace was clear. After switching static world
hulls to surface collision, that same 400-unit ray is clear with the road collider
enabled. An 86-point ground comparison found no missing physics surfaces. Its
single remaining mismatch hit a decorative agave visually and the ground underneath
physically. The former fall-through position hits the storm drain's visible floor.
These checks cover that neighbourhood, not every street in the game. Water,
interiors, traffic, distant skyline proxies and generated navigation are pending.

## Automated checks

Surface checks retain the original material bytes from COLL/COL2/COL3 faces and
primitives, parse the installed 179-entry audio table, and transfer labels onto
scaled and world-baked geometry without moving collision vertices. Ambiguous
alignments explicitly use the source's dominant group. In-engine ground rays
around Grove Street resolve concrete, grass and metal to three distinct footstep
events, each played through the native PlayerController method.

Loose-prop rules use `object.dat`, reject anchored special responses, immovable
mass sentinels, large geometry, world-baked pieces and skeletal models. The source
audit identifies 58 eligible models across 3,579 placements; qualification still
requires usable convex collision at decode time. Installed fixtures check rubbish
bags and cardboard boxes against their original mass and keep CJ's house and
terrain static. Map-prop changes currently persist within the streaming session;
save/reload and multiplayer replication of those changes are not implemented.

The actual Sandbox New game map row and mounted-source selector were exercised.
The launch result keeps the mounted scene path, the map loads through MapInstance
with a normal Sandbox player, and the map picker exposes one San Andreas entry
with a 1280×720 preview. Older city-start and source-build paths remain hidden
aliases. The editor's map-string widget checks for a `.vpk` and may show “Map is
not compiled”; the mounted SceneFile path loads directly without one.

`scripts/Release.ps1` runs the format, weapon, aiming, texture, prefab, presentation, vehicle, reader and bootstrap checks before packaging. Set `SADE_GAME_DIR` to the GTA installation directory to include the reader's installed-game checks.

The checks cover malformed animation and collision records, animation timing and bone aliases, reference poses, root-motion removal, mip ordering and compressed mip tails, stable prefab references, cache recovery, archive fingerprint changes and loading the mount's private dependencies. Installed-game checks also cover CJ's modular clothing, arm helper bones and inherited character material masks. All 9,199 selected speech-context references resolve to installed samples; fourteen voice and pain recordings are decoded as Ogg.

The installed-game audits read 281 animation banks, 1,101 native animation sequences, all 47 weapon definitions and the source gang relationships. These are parsing checks; they do not establish that every asset or weapon has been exercised in gameplay.

Pure presentation checks cover exact curated source identities, duplicate menu paths, NPC exclusions, vehicle family selection and locale-independent names. The curated selection excludes incomplete character parts, cutscene duplicates, trains, trailers and remote-control vehicles; their source models remain browsable under All.

## Editor checks

- The curated selection was verified at 322 choices: 67 NPCs, 191 vehicles and 64 scenery assets, with CJ and Big Smoke first. All adds the complete static and skeletal source model collection, including weapon models and items omitted from the curated selection. The seven top-level folders are All, Common buildings, Construction props, NPCs, Ramps, Street props and Vehicles. Sandbox's recursive root list includes both the curated choices and All; it is not limited to 322 models. CuratedBrowser.cs checks agreement between this tree and the Sandbox model list.
- Existing model, material, texture, sound, loadout and animation paths remain registered for dependencies and saved scenes. Browser registration uses metadata; it does not decode every model. The All aliases resolve the existing source resources. Legacy aliases and generated pedestrian/animation-bank combinations remain hidden, while the underlying source models are available through All.
- All 64 curated scenery models decoded with finite bounds, renderable geometry and nonempty collision. A private engine overview of the full selection was captured and inspected. This verifies the chosen scenery, rather than the game's entire environment catalog.
- CJ and Big Smoke report `flesh.surface` through their articulated limb colliders and fallback capsule. Sandbox's actual melee impact method selects the stock `ImpactHard` flesh sound and produces one blood burst plus one wound decal. Damage reaches the NPC controller and sets its attacker for retaliation. The test also repairs legacy generic collider surfaces without recreating, moving or unfreezing the rig; frozen posing remains intact, and bullet impacts receive no duplicate effects from damage handling. This invokes the real weapon methods in a temporary scene; it does not inject attack input or establish multiplayer behavior.
- Static props, vegetation and pedestrian materials, including a comparison of decoded source textures with GPU texture readback.
- Big Smoke and generic pedestrians: animated idle, navigation movement, facing direction and gang combat.
- CJ's vest and jeans use their modular player meshes, including the jeans' original UV layout. The dressed result was checked in an engine render. His DE arm helpers keep their reference pose. A live walk sampled over four seconds stayed in `WALK_player` through three complete loops without mid-cycle resets.
- Sandbox's false first-spawn prompt was reproduced by forcing NPC setup before `MountMetadata.OnStart`; the same timing check passes with the handoff fix.
- Fresh CJ and Big Smoke Props promote before their first physics step, stay upright from frame one and preserve spawn position, heading and scale. A separate case recovers Props that already simulated through their pelvis bone. No navigation mesh is present to hide an incorrect initial orientation.
- Vehicle thumbnails include the separate wheels. Infernus and BMX previews were checked through the same cache used by the spawn menu.
- In an isolated engine scene, a pedestrian stayed animated in idle without navigation, retried after cancellation, rejected a blocked route and walked again after the obstacle was removed.
- The native Unreal derrick animation advanced four articulated bones through its original 3.33-second sequence.
- CJ and Big Smoke's posing rigs passed checks for 16 bodies, 15 joints, animation alignment, stable falls and separately frozen limbs. A controller test exercised the physgun's native unfreeze operation, persistent posing, disabled navigation, damage and save/reload of all 16 frozen bodies. It did not simulate mouse input.
- Sound effects and a streamed radio track loaded through mounted `.vsnd` resources.
- Big Smoke automatically spoke his original nearby CHAT lines in a temporary scene. Fight, hit and death playback passed checks for head positioning, cooldowns, recent-clip avoidance, nearby-speaker limits and cleanup during posing, disable and shutdown. The playback check produced no console errors. Mounted sound duration metadata is not required.
- Bullet streaks render in the engine and stay on the actual hit ray, including stopping at a traced wall. Expiration, the 256-streak crowd limit and scene cleanup pass in a temporary scene.
- A real Sandbox mount spawn, automatic vehicle setup and seated test driver kept the same Infernus engine sound playing over 3.5 seconds. The existing game listener received it with full acoustic transmission. An isolated listener check also covered the driver seat and the car exterior. Normal-play feedback confirmed the sounds were audible; RPM and gear response are checked separately.
- Eighteen-second Infernus and Sanchez audio checks kept the same engine handles playing through all five source gears, with measured pitch drops at every upshift and nonzero playback amplitude.
- Vehicle attenuation checks preserve the car's gain at 50 units (0.712), while exterior gain at 300 units rises from 0.067 to 0.525. Source volume is unchanged. The helicopter check also preserves nearby gain.
- All nine full-size helicopters retain their actual skin-weighted blade geometry at rest and while spinning. Maverick and Hunter engine captures show the blades; an occupied Maverick spins up through its real seat and controller.
- Saved pedestrian and vehicle instances retained their transforms after an editor restart. Stable prefab IDs also preserve seat and exit references.
- Temporary-scene vehicle checks cover source mass, balance, propulsion, flotation and takeoff. They supply deterministic control values and are separate from testing the keyboard controls in play.
- A Squalo finds Sandbox's own water component without an injected cache, including a volume whose origin is below its surface. It travels 564 units in three seconds while floating. The same run passes for all six supported vehicle families.
- The Maverick was also confirmed working in normal play after the control, rotor and sound changes.
- A Dodo lost altitude without forward airspeed, turned in the direction of its bank and took off after building speed on a runway. These checks use engine physics with deterministic inputs.
- Sanchez and BMX stayed upright at rest and leaned into mirrored left/right turns. Their measured vertical speed during flat-ground turns stayed below 9 units per second.
- All twelve bikes with authored grip sockets fit the Human rider to the saddle and handlebars, including during translation, yaw and lean. The isolated engine check covers palm contact, unchanged arm lengths, clothing and attachment alignment, and restoring the normal pose after exit. NRG-500, Sanchez and BMX were also captured and inspected in engine renders.
- Car cabin extraction identified 142 steering rims among 147 non-RC road models. It isolates connected rim geometry instead of taking whole-material bounds, including cars whose wheel shares an interior material. Feet use rendered floor triangles rather than the car's enclosing physics hull. Pure tests cover stray trim, missing rims, invisible panels, malformed triangles and invalid rear-seat dummies; installed checks cover thirteen car models and the bus driver's footwell.
- Infernus, Premier, Banshee, Landstalker, Linerunner, Bus and Savanna passed temporary-scene occupant checks with the Human skeleton. Driver palms and supported-seat ankles follow their targets while the vehicle moves and turns; limb lengths, player/chair transforms and passenger arm poses stay intact. Feet point forward and poses clear after exit. Unsupported bus seats retain their ordinary animation. Infernus and Banshee engine renders were inspected. This does not establish every avatar's fit or multiplayer behavior.
- E's door-based seat selection passed checks for left/right entry, rotated vehicles, occupied seats, entry distance and bike rider/pillion access from both sides. Driver and passenger entry, seated collision, exit clearance and restored walking also passed the offline seat checks. These exercise the same selection helper as keyboard input without injecting a key press.
- The Infernus settles at the source suspension equilibrium of 72.7083 cm, with all four rendered tire bottoms on the floor. From 110 km/h it stops in 26.77 m / 1.76 s using the foot brake, or 33.22 m / 2.18 s using the rear handbrake. These are deterministic engine-physics measurements; keyboard feel remains a playtest judgment. The source invisible underbody material also retains its zero opacity.

The repeatable engine scripts are in [`tests/Sade.Mount.Integration`](../tests/Sade.Mount.Integration/README.md). They use the installed mount and real engine physics in temporary scenes.

## Remaining limits

Multiplayer ownership, replication and passenger behavior have not completed a two-client test. NPC speech and transient weapon effects currently run locally; playback on other clients is not implemented. Some cooked Unreal material graphs cannot be reconstructed fully. Character packed metallic/roughness maps are supported; other unmapped layouts and missing collision are reported explicitly. The installed `peds.col` contains malformed records, which are skipped individually so other collision can still load. Aircraft without authored collision use reported convex shapes derived from intact mesh parts.

Vehicle dynamics and weapon effects are s&box implementations driven by source data. They are not a reproduction of GTA's complete gameplay simulation.

Bike rider posing requires the Human skeleton and authored grip sockets. Pizzaboy has no grip sockets and keeps the standard seat pose; bike feet still use the standard seat animation. Car fitting also requires compatible limb bones and verified cabin geometry. Missing wheel or floor targets keep the corresponding ordinary pose. Feltzer's source seat sits above its wheel, so it receives foot placement without forced hand alignment; invalid bus entry/rear-seat dummies are not fitted to the underbody. Car wheels without a separate steering bone remain fixed, and hands follow the actual rim. These checks do not establish fit for every avatar proportion or multiplayer replication.
