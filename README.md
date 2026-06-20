# MC to TM
Converts Minecraft Java Edition worlds and schematics into TotalMiner save files.

## Features
- Full world conversion from MC world folder
- Schematic injection (`.schem`, `.litematic`, `.schematic`)
- Auto-centers the build using inhabited time weighting
- Ocean water filtering
- Save picker that finds your TotalMiner maps folder automatically

## Build
Requires .NET 8 SDK.
```
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=true -o bin/publish
```

Or just run it with `dotnet run`.

## Usage
Run the exe and follow the prompts, or use command line:
```
McToTm map <minecraft_world_folder> <tm_save_folder>
McToTm schematic <file.schem> <tm_save_folder> [x y z]
```

## Notes
- Back up your TotalMiner save before converting
- Large worlds take a few minutes depending on size
- Not every Minecraft block has a TotalMiner equivalent, unmapped blocks become air
