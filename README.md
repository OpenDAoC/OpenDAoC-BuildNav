# OpenDAoC-BuildNav

A fork of the [buildnav tool](https://github.com/thekroko/uthgard-opensource/tree/master/pathing/buildnav) from the [uthgard-opensource repository](https://github.com/thekroko/uthgard-opensource), originally created by [thekroko](https://github.com/thekroko).

## Prerequisites

1.  **64-bit Environment:** All components must be built and run as 64-bit.
2.  **GameServer DLL:** This project references `GameServer.dll` from the main [OpenDAoC-Core](https://github.com/OpenDAoC/OpenDAoC-Core) project to use its `LocalPathingMgr`. Copy the resulting 64-bit into this project's `base` directory.
3.  **Detour DLL:** Build `Detour.dll` from the [OpenDAoC-Core Detour project](https://github.com/OpenDAoC/OpenDAoC-Core/tree/master/Pathing/Detour). Copy the resulting 64-bit into this project's `base` directory.

## How it works

1.  The tool reads game asset files from DAoC’s `zones` folder.
2.  The processed data is fed into `RecastDemo.exe` to generate initial navigation meshes.
3.  The final navigation meshes (`*.nav`) are used by the game server.

## Recast integration

Recast is included as a precompiled executable in the `base` directory.

This `RecastDemo.exe` is a repurposed 64-bit build of the [official RecastDemo](https://github.com/recastnavigation/recastnavigation/tree/main/RecastDemo), slightly tweaked to accept command-line arguments and handle large worlds.
Parameters for navmesh generation (agent size, etc.) are currently hardcoded and not configurable.

It can also be used to manually inspect the generated meshes:
1.  Open `RecastDemo.exe` from the output directory.
2.  Select "Tile Mesh" as the sample.
3.  Select any generated `*.gset` as the input mesh.
4.  The corresponding `*.nav` file will also be loaded for inspection.

## Usage instructions

1.  Build and run `buildnav.exe --daoc=<gamedir>`. Use either `--all=true`, or `--zones="id1,id2..."` and/or `--regions="id1,id2..."`.
2.  Copy the generated `*.nav` files from the output directory into your server's `/pathing` directory.  

## Output folder structure

```
bin/Release/.../
├── OpenDAoC-BuildNav.exe
└── base/                  # Copied from the project's `base` folder
    ├── RecastDemo.exe     # Pre-compiled and tweaked Recast executable
    └── zones/             # Generated intermediate files and navigation meshes (*.nav)
```