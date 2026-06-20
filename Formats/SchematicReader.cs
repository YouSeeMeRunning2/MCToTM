using fNbt;
using McToTm.Core;

namespace McToTm.Formats;

public static class SchematicReader
{
    public static byte[,,] LoadSchematicGrid(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".schem" => ReadSchemGrid(path),
            ".litematic" => ReadLitematicGrid(path),
            ".schematic" => ReadClassicSchematicGrid(path),
            _ => throw new NotSupportedException($"Unsupported format: {ext}")
        };
    }

    private static byte[,,] ReadSchemGrid(string path)
    {
        var nbtFile = new NbtFile();
        nbtFile.LoadFromFile(path);
        var root = nbtFile.RootTag;

        if (root["Schematic"] is NbtCompound schematic)
            root = schematic;

        int width = root["Width"]!.ShortValue;
        int height = root["Height"]!.ShortValue;
        int length = root["Length"]!.ShortValue;

        NbtCompound? blocksCompound = root["Blocks"] as NbtCompound;
        NbtCompound? paletteTag;
        byte[] blockDataRaw;

        if (blocksCompound != null && blocksCompound["Palette"] != null)
        {
            paletteTag = blocksCompound["Palette"] as NbtCompound;
            blockDataRaw = (blocksCompound["Data"] as NbtByteArray)!.Value;
        }
        else
        {
            paletteTag = root["Palette"] as NbtCompound;
            blockDataRaw = (root["BlockData"] as NbtByteArray)!.Value;
        }

        var palette = new Dictionary<int, string>();
        if (paletteTag != null)
        {
            foreach (var tag in paletteTag)
                palette[tag.IntValue] = tag.Name ?? "";
        }

        int maxIdx = palette.Count > 0 ? palette.Keys.Max() : 0;
        var paletteTm = new byte[maxIdx + 1];
        foreach (var (idx, blockState) in palette)
        {
            var (name, props) = ParseBlockId(blockState);
            if (!BlockMapping.IsSkipBlock(name))
                paletteTm[idx] = BlockMapping.ResolveMcBlock(name, props);
        }

        int total = width * height * length;
        var indices = ReadVarintArray(blockDataRaw, total);

        var grid = new byte[width, height, length];
        for (int i = 0; i < indices.Length && i < total; i++)
        {
            int x = i % width;
            int z = (i / width) % length;
            int y = i / (width * length);
            int palIdx = Math.Clamp(indices[i], 0, paletteTm.Length - 1);
            grid[x, y, z] = paletteTm[palIdx];
        }
        return grid;
    }

    private static byte[,,] ReadLitematicGrid(string path)
    {
        var nbtFile = new NbtFile();
        nbtFile.LoadFromFile(path);
        var root = nbtFile.RootTag;

        NbtCompound? regions = null;
        if (root["Regions"] is NbtCompound r1) regions = r1;
        else if (root[""] is NbtCompound inner && inner["Regions"] is NbtCompound r2) regions = r2;
        else
        {
            foreach (var tag in root)
            {
                if (tag is NbtCompound c && c["Regions"] is NbtCompound r3)
                { regions = r3; break; }
            }
        }
        if (regions == null) throw new InvalidDataException("Cannot find Regions in litematic");

        int gMinX = int.MaxValue, gMinY = int.MaxValue, gMinZ = int.MaxValue;
        int gMaxX = int.MinValue, gMaxY = int.MinValue, gMaxZ = int.MinValue;

        var regionInfos = new List<(int ox, int oy, int oz, int sx, int sy, int sz, NbtCompound region)>();

        foreach (NbtCompound region in regions)
        {
            var posTag = (NbtCompound)region["Position"]!;
            var sizeTag = (NbtCompound)region["Size"]!;
            int rx = posTag["x"]!.IntValue, ry = posTag["y"]!.IntValue, rz = posTag["z"]!.IntValue;
            int sxr = sizeTag["x"]!.IntValue, syr = sizeTag["y"]!.IntValue, szr = sizeTag["z"]!.IntValue;
            int absSx = Math.Abs(sxr), absSy = Math.Abs(syr), absSz = Math.Abs(szr);
            int ox = sxr >= 0 ? rx : rx + sxr + 1;
            int oy = syr >= 0 ? ry : ry + syr + 1;
            int oz = szr >= 0 ? rz : rz + szr + 1;

            gMinX = Math.Min(gMinX, ox); gMinY = Math.Min(gMinY, oy); gMinZ = Math.Min(gMinZ, oz);
            gMaxX = Math.Max(gMaxX, ox + absSx - 1);
            gMaxY = Math.Max(gMaxY, oy + absSy - 1);
            gMaxZ = Math.Max(gMaxZ, oz + absSz - 1);
            regionInfos.Add((ox, oy, oz, absSx, absSy, absSz, region));
        }

        int gridX = gMaxX - gMinX + 1;
        int gridY = gMaxY - gMinY + 1;
        int gridZ = gMaxZ - gMinZ + 1;
        var grid = new byte[gridX, gridY, gridZ];

        foreach (var (ox, oy, oz, absSx, absSy, absSz, region) in regionInfos)
        {
            var paletteList = (NbtList)region["BlockStatePalette"]!;
            int palSize = paletteList.Count;
            var paletteTm = new byte[palSize];
            for (int pi = 0; pi < palSize; pi++)
            {
                var entry = (NbtCompound)paletteList[pi];
                string bname = entry["Name"]!.StringValue;
                var props = new Dictionary<string, string>();
                if (entry["Properties"] is NbtCompound propsTag)
                    foreach (var p in propsTag)
                        props[p.Name ?? ""] = p.StringValue;
                var (name, extraProps) = ParseBlockId(bname);
                foreach (var kv in extraProps) props[kv.Key] = kv.Value;
                if (!BlockMapping.IsSkipBlock(name))
                    paletteTm[pi] = BlockMapping.ResolveMcBlock(name, props);
            }

            int total = absSx * absSy * absSz;
            int bits = Math.Max(2, TmRegion.BitLength(palSize - 1));
            var blockStates = (NbtLongArray)region["BlockStates"]!;
            var indices = TmRegion.UnpackDenseBitArray(blockStates.Value, bits, total);

            for (int i = 0; i < total; i++)
            {
                int yLocal = i / (absSx * absSz);
                int remainder = i % (absSx * absSz);
                int zLocal = remainder / absSx;
                int xLocal = remainder % absSx;

                int palIdx = Math.Clamp(indices[i], 0, palSize - 1);
                byte tmId = paletteTm[palIdx];
                if (tmId == 0) continue;

                int gx = (ox - gMinX) + xLocal;
                int gy = (oy - gMinY) + yLocal;
                int gz = (oz - gMinZ) + zLocal;
                if (gx >= 0 && gx < gridX && gy >= 0 && gy < gridY && gz >= 0 && gz < gridZ)
                    grid[gx, gy, gz] = Math.Max(grid[gx, gy, gz], tmId);
            }
        }
        return grid;
    }

    private static byte[,,] ReadClassicSchematicGrid(string path)
    {
        var nbtFile = new NbtFile();
        nbtFile.LoadFromFile(path);
        var root = nbtFile.RootTag;
        if (root["Schematic"] is NbtCompound schematic)
            root = schematic;

        int width = root["Width"]!.ShortValue;
        int height = root["Height"]!.ShortValue;
        int length = root["Length"]!.ShortValue;

        var blocksRaw = (NbtByteArray)root["Blocks"]!;

        var numericIds = new Dictionary<int, string>
        {
            [1] = "stone", [2] = "grass_block", [3] = "dirt", [4] = "cobblestone",
            [5] = "oak_planks", [7] = "bedrock", [8] = "water", [9] = "water",
            [12] = "sand", [13] = "gravel", [14] = "gold_ore", [15] = "iron_ore",
            [16] = "coal_ore", [17] = "oak_log", [18] = "oak_leaves", [20] = "glass",
            [24] = "sandstone", [35] = "white_wool", [44] = "stone_slab",
            [45] = "bricks", [48] = "mossy_cobblestone", [49] = "obsidian", [50] = "torch",
            [53] = "oak_stairs", [54] = "chest", [58] = "crafting_table", [61] = "furnace",
            [64] = "oak_door", [65] = "ladder", [67] = "cobblestone_stairs", [71] = "iron_door",
            [79] = "ice", [80] = "snow_block", [85] = "oak_fence", [86] = "pumpkin",
            [89] = "glowstone", [98] = "stone_bricks", [101] = "iron_bars", [102] = "glass_pane",
            [126] = "oak_slab", [134] = "spruce_stairs", [135] = "birch_stairs",
            [136] = "jungle_stairs", [139] = "cobblestone_wall", [155] = "quartz_block",
            [159] = "white_terracotta", [160] = "white_stained_glass_pane", [170] = "hay_block",
            [172] = "terracotta", [43] = "stone_slab",
        };

        var grid = new byte[width, height, length];
        for (int y = 0; y < height; y++)
        for (int z = 0; z < length; z++)
        for (int x = 0; x < width; x++)
        {
            int idx = (y * length + z) * width + x;
            int blockId = blocksRaw.Value[idx] & 0xFF;
            if (blockId == 0) continue;
            if (numericIds.TryGetValue(blockId, out string? name))
            {
                if (!BlockMapping.IsSkipBlock(name))
                    grid[x, y, z] = BlockMapping.ResolveMcBlock(name, null);
            }
            else
            {
                grid[x, y, z] = BlockMapping.ResolveMcBlock("cobblestone", null);
            }
        }
        return grid;
    }

    private static int[] ReadVarintArray(byte[] data, int length)
    {
        var result = new int[length];
        int pos = 0, ri = 0;
        while (ri < length && pos < data.Length)
        {
            int value = 0, shift = 0;
            while (pos < data.Length)
            {
                byte b = data[pos++];
                value |= (b & 0x7F) << shift;
                if ((b & 0x80) == 0) break;
                shift += 7;
            }
            result[ri++] = value;
        }
        return result;
    }

    public static (string name, Dictionary<string, string> props) ParseBlockId(string rawId)
    {
        string name = rawId.Trim().ToLowerInvariant();
        if (name.StartsWith("minecraft:")) name = name[10..];
        var props = new Dictionary<string, string>();
        int bracket = name.IndexOf('[');
        if (bracket >= 0)
        {
            string propStr = name[(bracket + 1)..].TrimEnd(']');
            name = name[..bracket];
            foreach (string pair in propStr.Split(','))
            {
                int eq = pair.IndexOf('=');
                if (eq >= 0)
                    props[pair[..eq].Trim()] = pair[(eq + 1)..].Trim();
            }
        }
        return (name.Trim(), props);
    }
}