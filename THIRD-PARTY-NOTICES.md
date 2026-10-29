# Third-party notices

## Unreal package reader

This project references **CUE4Parse** version `1.2.2.202610` by FabianFG. Its NuGet metadata identifies the license as Apache-2.0 and the source commit as `1e3293d4b3ecaf6ef3d73641b9ffe47fa4cf1562` in [FabianFG/CUE4Parse](https://github.com/FabianFG/CUE4Parse).

Eight animation conversion/support source files are vendored from the same pinned upstream commit under `src/Sade.Reader/ThirdParty/CUE4Parse`. Their `ORIGIN.md` records the original paths and attribution-only edits; `LICENSE` contains the complete upstream Apache-2.0 license and `NOTICE` preserves upstream attributions. All three files are copied to the built package's `third-party/CUE4Parse-vendored-animation` directory. The separate CUE4Parse-Conversion NuGet package is not used.

Copyright (c) 2026 FabianFG. The Apache License, Version 2.0 is included in `ThirdParty/Apache-2.0.txt` and in the built package's `third-party` directory.

The reader also brings transitive packages for parsing, compression, cryptography, textures, and audio. `scripts/Write-ThirdPartyInventory.ps1` reads the actual NuGet restore manifest and records every restored package/version, declared license, and source/project URL in the built package's `third-party/packages.json`. It copies each package's NuGet metadata and included LICENSE, NOTICE, or COPYING files into that directory. This inventory follows the resolved dependency versions instead of a manually maintained list.

## s&box

The project builds against your installed s&box version. The build/install scripts do not redistribute engine assemblies. API references were checked against a local copy of the s&box source and API docs. The engine and its assets remain subject to their own licenses.

## Reference mount

[K3rhos/alan_wake](https://github.com/K3rhos/alan_wake) was used as an architectural reference for native mount packaging and resource loading. Its repository reported no license at the time it was inspected; no Alan Wake parser or game assets are included in this project.

## Game data and decoder

Grand Theft Auto: San Andreas — The Definitive Edition data stays in your game installation. No Rockstar game models, textures, animations, audio, archive files, or Oodle decoder DLL are distributed with this repository. The reader opens the installed game and loads its decoder from that installation. Rockstar, GTA, Unreal Engine, and Oodle names identify their respective third-party products; this project is not an official release of those products.
