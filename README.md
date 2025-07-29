# OpenDAoC-BuildNav

A fork of the [buildnav tool](https://github.com/thekroko/uthgard-opensource/tree/master/pathing/buildnav) from the [uthgard-opensource repository](https://github.com/thekroko/uthgard-opensource), originally created by [thekroko](https://github.com/thekroko).  


## How it works  

1. The tool reads files from DAoC’s `zones` folder.  
2. The processed data is immediately fed into Recast to generate navigation meshes.  
3. The navigation meshes are used by the game server via [Detour](https://github.com/OpenDAoC/OpenDAoC-Core/tree/master/Pathing/Detour) (needs to be built separately).


## Recast integration  

Recast is included as a precompiled executable in the `base` directory.  
The `base` directory is copied to the output directly when the project is built.  

It can also be used to inspect meshes:  
1. Open `buildnav.exe` in the output directory.  
2. Select "Tile Mesh" as the sample.  
3. Select any generated `*.gset` as the input mesh.  
4. The corresponding `*.nav` will also be loaded.  


## Usage instructions  

1. Build and run `OpenDAoC-BuildNav.exe --daoc=<gamedir>`. Use either `--all=true`, or `--zones="id1,id2..."` and/or `--regions="id1,id2..."`.
2. Copy the generated `*.nav` files from the output directory into your server's `/pathing` directory.  


## Output folder structure  

```
bin/Release/.../  
├── OpenDAoC-BuildNav.exe  
└── base/                  # Copied from `base`  
    ├── buildnav.exe       # Pre-compiled (and slighly tweaked) Recast executable.  
    └── zones/             # Generated intermediaty files and navigation meshes (*.nav)  
```
