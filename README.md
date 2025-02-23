# OpenDAoC-BuildNav

This is a fork of the [buildnav tool](https://github.com/thekroko/uthgard-opensource/tree/master/pathing/buildnav) from the [uthgard-opensource repository](https://github.com/thekroko/uthgard-opensource), originally created by [thekroko](https://github.com/thekroko).

The tool generates Recast navmeshes for DAoC zones.

## Usage instructions

1. Run `buildnav.exe --daoc=<gamedir> --obj=false --all=true` to build navmeshes (this will take a long time).
2. Copy `*.nav` files into your server's `/pathing` directory.
