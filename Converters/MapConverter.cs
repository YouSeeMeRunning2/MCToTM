using fNbt;
using McToTm.Core;
using McToTm.Formats;

namespace McToTm.Converters;

public class MapConverter
{
    private const int TmMaxY = 510;
    private const int TmMapHalf = 4096;
    private const int YOffset = 0;
    private const int McMinSectionY = 2;
    private const int CS = TmRegion.ChunkSize;
    private const int CS3 = CS * CS * CS;

    public bool SkipUninhabited { get; set; } = true;
    public bool FilterOceanWater { get; set; } = true;
    public int OceanThreshold { get; set; } = 100_000;

    private readonly Dictionary<(int, int, int), byte[]> _chunkMap = new();
    private readonly Dictionary<(int, int, int), ushort[]> _auxMap = new();

    public void Convert(string worldDir, string saveDir, Action<string>? log = null)
    {
        log ??= Console.WriteLine;
        string regionDir = Path.Combine(worldDir, "region");
        if (!Directory.Exists(regionDir))
            throw new DirectoryNotFoundException($"No region/ folder in {worldDir}");

        var header = TmRegion.ParseHeaderDat(Path.Combine(saveDir, "header.dat"));
        log($"TotalMiner world : {header.MapName}");
        log($"Bounds           : ({header.MapBoundMin.x},{header.MapBoundMin.y},{header.MapBoundMin.z}) to ({header.MapBoundMax.x},{header.MapBoundMax.y},{header.MapBoundMax.z})");
        log($"Region size      : ({header.RegionSize.x},{header.RegionSize.y},{header.RegionSize.z})");
        log("");

        var (xOff, zOff) = ComputeOffsets(regionDir, log);

        var mcaFiles = Directory.GetFiles(regionDir, "r.*.*.mca")
            .Where(f => new FileInfo(f).Length > 4096)
            .OrderByDescending(f => new FileInfo(f).Length)
            .ToList();
        log($"Found {mcaFiles.Count} non-empty .mca files");

        var existingRegs = Directory.GetFiles(saveDir, "*.reg");
        if (existingRegs.Length > 0)
        {
            log($"Deleting {existingRegs.Length} existing .reg files...");
            foreach (var f in existingRegs) File.Delete(f);
        }

        _chunkMap.Clear();
        _auxMap.Clear();
        int totalSections = 0, skippedUninhabited = 0, skippedOob = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        for (int fi = 0; fi < mcaFiles.Count; fi++)
        {
            var parts = Path.GetFileNameWithoutExtension(mcaFiles[fi]).Split('.');
            int regionRx = int.Parse(parts[1]);
            int regionRz = int.Parse(parts[2]);

            foreach (var (cxLocal, czLocal, nbt) in McaReader.ReadChunks(mcaFiles[fi]))
            {
                int mcCx = regionRx * 32 + cxLocal;
                int mcCz = regionRz * 32 + czLocal;

                bool isNew = nbt["sections"] != null;
                long inhabited;
                NbtList? sections;

                if (isNew)
                {
                    inhabited = nbt["InhabitedTime"]?.LongValue ?? 0;
                    sections = nbt["sections"] as NbtList;
                }
                else
                {
                    var level = nbt["Level"] as NbtCompound ?? nbt;
                    inhabited = level["InhabitedTime"]?.LongValue ?? 0;
                    sections = level["Sections"] as NbtList;
                }

                if (SkipUninhabited && inhabited == 0) { skippedUninhabited++; continue; }

                int chunkTmMinX = mcCx * 16 + xOff;
                int chunkTmMinZ = mcCz * 16 + zOff;
                if (chunkTmMinX >= TmMapHalf || chunkTmMinX + 16 <= -TmMapHalf ||
                    chunkTmMinZ >= TmMapHalf || chunkTmMinZ + 16 <= -TmMapHalf)
                { skippedOob++; continue; }

                if (sections == null) continue;

                foreach (NbtCompound sec in sections)
                {
                    int secY = sec["Y"]?.ByteValue ?? sec["Y"]?.IntValue ?? 0;
                    if (secY < McMinSectionY) continue;

                    var (blocks, aux) = DecodeSection(sec, isNew);
                    if (blocks == null) continue;

                    PlaceSection(mcCx * 16, mcCz * 16, secY, blocks, aux, xOff, zOff);
                    totalSections++;
                }
            }

            if ((fi + 1) % 5 == 0 || fi + 1 == mcaFiles.Count)
            {
                double pct = (fi + 1.0) / mcaFiles.Count * 100;
                log($"  [{fi + 1}/{mcaFiles.Count}] {pct:F0}%  chunks={_chunkMap.Count:N0}  {sw.Elapsed.TotalSeconds:F0}s");
            }
        }

        log($"\nParsed {totalSections:N0} sections -> {_chunkMap.Count:N0} TM chunks  ({sw.Elapsed.TotalSeconds:F1}s)");

        if (FilterOceanWater)
        {
            log("\nFiltering open water...");
            RemoveOceanWater(log);
        }

        log("\nWriting region files...");
        WriteRegions(saveDir, header.RegionSize, log);
        TmRegion.TouchHeaderDat(Path.Combine(saveDir, "header.dat"));
        log($"\nTotal elapsed: {sw.Elapsed.TotalSeconds:F1}s");
        log($"World written to: {saveDir}");
    }

    private (int xOff, int zOff) ComputeOffsets(string regionDir, Action<string> log)
    {
        log("Pre-scanning for optimal centering...");
        double cxSum = 0, czSum = 0, totalIt = 0;
        int chunksScanned = 0;

        foreach (var mcaPath in Directory.GetFiles(regionDir, "r.*.*.mca"))
        {
            if (new FileInfo(mcaPath).Length < 4096) continue;
            var parts = Path.GetFileNameWithoutExtension(mcaPath).Split('.');
            if (parts.Length != 3) continue;
            int rx = int.Parse(parts[1]), rz = int.Parse(parts[2]);

            try
            {
                using var fs = File.OpenRead(mcaPath);
                var header = new byte[8192];
                fs.ReadExactly(header);

                for (int i = 0; i < 1024; i++)
                {
                    int offSec = (int)(ReadBigEndianUInt32(header, i * 4) >> 8) & 0xFFFFFF;
                    if (offSec == 0) continue;

                    try
                    {
                        byte[] raw = McaReader.DecompressChunkRaw(mcaPath, offSec);
                        long it = McaReader.FastInhabitedTime(raw);
                        if (it <= 0) continue;

                        int mcBx = (rx * 32 + (i % 32)) * 16 + 8;
                        int mcBz = (rz * 32 + (i / 32)) * 16 + 8;
                        cxSum += mcBx * (double)it;
                        czSum += mcBz * (double)it;
                        totalIt += it;
                        chunksScanned++;
                    }
                    catch { }
                }
            }
            catch { }
        }

        double centroidX, centroidZ;
        if (totalIt == 0)
        {
            log("  No InhabitedTime data, using file-size centroid");
            double txs = 0, tzs = 0, tw = 0;
            foreach (var p in Directory.GetFiles(regionDir, "r.*.*.mca"))
            {
                var fi2 = new FileInfo(p);
                if (fi2.Length < 4096) continue;
                var parts2 = Path.GetFileNameWithoutExtension(p).Split('.');
                if (parts2.Length != 3) continue;
                txs += (int.Parse(parts2[1]) * 512 + 256) * (double)fi2.Length;
                tzs += (int.Parse(parts2[2]) * 512 + 256) * (double)fi2.Length;
                tw += fi2.Length;
            }
            centroidX = txs / tw;
            centroidZ = tzs / tw;
        }
        else
        {
            centroidX = cxSum / totalIt;
            centroidZ = czSum / totalIt;
            log($"  Scanned {chunksScanned:N0} inhabited chunks");
        }

        int xOff = -(int)Math.Round(centroidX);
        int zOff = -(int)Math.Round(centroidZ);
        log($"  Centroid: X={centroidX:F0} Z={centroidZ:F0}  Offset: {xOff}, {zOff}");
        return (xOff, zOff);
    }

    private static (byte[]? blocks, ushort[]? aux) DecodeSection(NbtCompound section, bool isNew)
    {
        try
        {
            NbtList? palette;
            long[]? dataLongs;

            if (isNew)
            {
                var bs = section["block_states"] as NbtCompound;
                if (bs == null) return (null, null);
                palette = bs["palette"] as NbtList;
                dataLongs = (bs["data"] as NbtLongArray)?.Value;
            }
            else
            {
                if (section["Blocks"] is NbtByteArray blocksTag)
                    return DecodeNumericSection(blocksTag.Value, (section["Data"] as NbtByteArray)?.Value);
                palette = section["Palette"] as NbtList;
                dataLongs = (section["BlockStates"] as NbtLongArray)?.Value;
            }

            if (palette == null || palette.Count == 0) return (null, null);

            var tmPal = ResolvePalette(palette);

            if (dataLongs == null || dataLongs.Length == 0)
            {
                byte tmId = tmPal.Length > 0 ? tmPal[0] : (byte)0;
                if (tmId == 0) return (null, null);
                var fill = new byte[4096];
                Array.Fill(fill, tmId);
                return (fill, null);
            }

            var indices = TmRegion.UnpackBitArray(dataLongs, 4096, palette.Count);
            var blocks = MapIndicesToBlocks(indices, tmPal);
            if (blocks == null) return (null, null);

            var aux = BuildAux(palette, indices);
            return (blocks, aux);
        }
        catch { return (null, null); }
    }

    private static byte[]? MapIndicesToBlocks(int[] indices, byte[] palette)
    {
        var blocks = new byte[indices.Length];
        bool any = false;
        for (int i = 0; i < indices.Length; i++)
        {
            blocks[i] = palette[Math.Clamp(indices[i], 0, palette.Length - 1)];
            if (blocks[i] != 0) any = true;
        }
        return any ? blocks : null;
    }

    private static byte[] ResolvePalette(NbtList palette)
    {
        var result = new byte[palette.Count];
        for (int i = 0; i < palette.Count; i++)
        {
            var (name, props) = ParsePaletteEntry((NbtCompound)palette[i]);
            result[i] = BlockMapping.ResolveMcBlock(name, props);
        }
        return result;
    }

    private static ushort[]? BuildAux(NbtList palette, int[] indices)
    {
        var aux = new ushort[4096];
        bool hasAux = false;

        for (int pi = 0; pi < palette.Count; pi++)
        {
            var (name, props) = ParsePaletteEntry((NbtCompound)palette[pi]);
            ushort av = ComputeAux(name, props);
            if (av == 0) continue;

            hasAux = true;
            for (int i = 0; i < 4096; i++)
                if (indices[i] == pi) aux[i] = av;
        }
        return hasAux ? aux : null;
    }

    private static (string name, Dictionary<string, string> props) ParsePaletteEntry(NbtCompound entry)
    {
        string name = entry["Name"]!.StringValue;
        if (name.StartsWith("minecraft:")) name = name[10..];
        var props = new Dictionary<string, string>();
        if (entry["Properties"] is NbtCompound propsTag)
            foreach (var p in propsTag)
                props[p.Name ?? ""] = p.StringValue;
        return (name, props);
    }

    private static ushort ComputeAux(string name, Dictionary<string, string> props)
    {
        if (name.EndsWith("_stairs") || name == "stairs")
        {
            ushort dir = FacingToTm.GetValueOrDefault(props.GetValueOrDefault("facing", "east"), (ushort)0);
            ushort upside = (ushort)(props.GetValueOrDefault("half", "bottom") == "top" ? 4 : 0);
            return (ushort)(dir + upside);
        }
        if (name.EndsWith("_slab"))
            return (ushort)(props.GetValueOrDefault("type", "bottom") == "top" ? 1 : 0);
        if (name.EndsWith("_trapdoor"))
        {
            ushort tmDir = FacingTdToTm.GetValueOrDefault(props.GetValueOrDefault("facing", "north"), (ushort)3);
            if (props.GetValueOrDefault("open", "false") == "true")
                return TmTdOpen[tmDir];
            return tmDir;
        }
        return 0;
    }

    private static readonly Dictionary<string, ushort> FacingToTm = new()
    { ["east"] = 0, ["south"] = 1, ["west"] = 2, ["north"] = 3 };

    private static readonly Dictionary<string, ushort> FacingTdToTm = new()
    { ["north"] = 3, ["south"] = 1, ["east"] = 0, ["west"] = 2 };

    private static readonly ushort[] TmTdOpen = { 5, 6, 7, 4 };

    private static (byte[]? blocks, ushort[]? aux) DecodeNumericSection(byte[] blocksRaw, byte[]? dataRaw)
    {
        if (blocksRaw.Length != 4096) return (null, null);
        var dataArr = new byte[4096];
        if (dataRaw != null && dataRaw.Length >= 2048)
            for (int i = 0; i < 2048; i++)
            {
                dataArr[i * 2] = (byte)(dataRaw[i] & 0x0F);
                dataArr[i * 2 + 1] = (byte)((dataRaw[i] >> 4) & 0x0F);
            }

        var lut = GetOldIdLut();
        var blocks = new byte[4096];
        bool any = false;
        for (int i = 0; i < 4096; i++)
        {
            blocks[i] = lut[blocksRaw[i], dataArr[i]];
            if (blocks[i] != 0) any = true;
        }
        return any ? (blocks, (ushort[]?)null) : (null, null);
    }

    private static byte[,]? _oldIdLut;
    private static byte[,] GetOldIdLut()
    {
        if (_oldIdLut != null) return _oldIdLut;
        _oldIdLut = new byte[256, 16];

        var simple = new Dictionary<int, string>
        {
            [1] = "stone", [2] = "grass_block", [3] = "dirt", [4] = "cobblestone",
            [5] = "oak_planks", [7] = "bedrock", [8] = "water", [9] = "water",
            [12] = "sand", [13] = "gravel", [14] = "gold_ore", [15] = "iron_ore",
            [16] = "coal_ore", [17] = "oak_log", [18] = "oak_leaves", [19] = "sponge",
            [20] = "glass", [21] = "lapis_ore", [22] = "lapis_block", [23] = "dispenser",
            [24] = "sandstone", [25] = "note_block", [29] = "piston", [30] = "cobweb",
            [33] = "piston", [41] = "gold_block", [42] = "iron_block",
            [45] = "bricks", [46] = "tnt", [47] = "bookshelf", [48] = "mossy_cobblestone",
            [49] = "obsidian", [50] = "torch", [53] = "oak_stairs", [54] = "chest",
            [56] = "diamond_ore", [57] = "diamond_block", [58] = "crafting_table",
            [60] = "farmland", [61] = "furnace", [62] = "furnace", [64] = "oak_door",
            [65] = "ladder", [67] = "cobblestone_stairs", [71] = "iron_door",
            [73] = "redstone_ore", [74] = "redstone_ore", [78] = "snow", [79] = "ice",
            [80] = "snow_block", [81] = "cactus", [82] = "clay", [84] = "jukebox",
            [85] = "oak_fence", [86] = "pumpkin", [87] = "netherrack", [88] = "soul_sand",
            [89] = "glowstone", [91] = "jack_o_lantern", [96] = "oak_trapdoor",
            [101] = "iron_bars", [102] = "glass_pane", [103] = "melon",
            [106] = "vine", [107] = "oak_fence_gate", [108] = "brick_stairs",
            [109] = "stone_brick_stairs", [110] = "mycelium", [112] = "nether_bricks",
            [113] = "nether_brick_fence", [114] = "nether_brick_stairs",
            [116] = "enchanting_table", [121] = "end_stone", [123] = "redstone_lamp",
            [124] = "redstone_lamp", [128] = "sandstone_stairs", [129] = "emerald_ore",
            [130] = "ender_chest", [133] = "emerald_block", [134] = "spruce_stairs",
            [135] = "birch_stairs", [136] = "jungle_stairs", [138] = "beacon",
            [139] = "cobblestone_wall", [145] = "anvil", [146] = "chest",
            [152] = "redstone_block", [153] = "quartz_ore", [156] = "quartz_stairs",
            [163] = "acacia_stairs", [164] = "dark_oak_stairs", [165] = "slime_block",
            [167] = "iron_trapdoor", [169] = "sea_lantern", [170] = "hay_block",
            [172] = "terracotta", [173] = "coal_block", [174] = "packed_ice",
            [180] = "red_sandstone_stairs", [198] = "end_rod",
            [199] = "chorus_plant", [200] = "chorus_flower", [201] = "purpur_block",
            [202] = "purpur_pillar", [203] = "purpur_stairs", [206] = "end_stone_bricks",
            [208] = "dirt_path", [212] = "ice", [213] = "magma_block",
            [214] = "nether_wart_block", [215] = "red_nether_bricks", [216] = "bone_block",
            [218] = "observer",
        };

        foreach (var (bid, name) in simple)
            for (int d = 0; d < 16; d++)
                _oldIdLut[bid, d] = BlockMapping.ResolveMcBlock(name);

        FillColorLut(35, "white_wool", "orange_wool", "magenta_wool", "light_blue_wool",
            "yellow_wool", "lime_wool", "pink_wool", "gray_wool",
            "light_gray_wool", "cyan_wool", "purple_wool", "blue_wool",
            "brown_wool", "green_wool", "red_wool", "black_wool");

        FillColorLut(159, "white_terracotta", "orange_terracotta", "magenta_terracotta",
            "light_blue_terracotta", "yellow_terracotta", "lime_terracotta",
            "pink_terracotta", "gray_terracotta", "light_gray_terracotta",
            "cyan_terracotta", "purple_terracotta", "blue_terracotta",
            "brown_terracotta", "green_terracotta", "red_terracotta", "black_terracotta");

        string[] concrete = { "white_concrete","orange_concrete","magenta_concrete","light_blue_concrete",
            "yellow_concrete","lime_concrete","pink_concrete","gray_concrete",
            "light_gray_concrete","cyan_concrete","purple_concrete","blue_concrete",
            "brown_concrete","green_concrete","red_concrete","black_concrete" };
        FillColorLut(251, concrete);
        FillColorLut(252, concrete);

        return _oldIdLut;
    }

    private static void FillColorLut(int blockId, params string[] names)
    {
        for (int d = 0; d < 16 && d < names.Length; d++)
            _oldIdLut![blockId, d] = BlockMapping.ResolveMcBlock(names[d]);
    }

    private void PlaceSection(int mcWorldBx, int mcWorldBz, int secY, byte[] blocks, ushort[]? aux, int xOff, int zOff)
    {
        int tmBx0 = mcWorldBx + xOff;
        int tmBz0 = mcWorldBz + zOff;
        int tmBy0 = secY * 16 + YOffset;

        if (tmBy0 + 16 <= 0 || tmBy0 >= TmMaxY) return;

        for (int ly = 0; ly < 16; ly++)
        for (int lz = 0; lz < 16; lz++)
        for (int lx = 0; lx < 16; lx++)
        {
            int srcIdx = ly * 256 + lz * 16 + lx;
            byte bid = blocks[srcIdx];
            if (bid == 0) continue;

            int tmBx = tmBx0 + lx, tmBy = tmBy0 + ly, tmBz = tmBz0 + lz;
            if (tmBy < 0 || tmBy >= TmMaxY) continue;
            if (tmBx < -TmMapHalf || tmBx >= TmMapHalf) continue;
            if (tmBz < -TmMapHalf || tmBz >= TmMapHalf) continue;

            int cgx = TmRegion.ChunkBase(tmBx);
            int cgy = (tmBy / CS) * CS;
            int cgz = TmRegion.ChunkBase(tmBz);
            int lin = TmRegion.LinearIndex(tmBx - cgx, tmBy - cgy, tmBz - cgz);

            var key = (cgx, cgy, cgz);
            if (!_chunkMap.TryGetValue(key, out var chunk))
            {
                chunk = new byte[CS3];
                _chunkMap[key] = chunk;
            }
            chunk[lin] = bid;

            if (aux != null && aux[srcIdx] != 0)
            {
                if (!_auxMap.TryGetValue(key, out var auxChunk))
                {
                    auxChunk = new ushort[CS3];
                    _auxMap[key] = auxChunk;
                }
                auxChunk[lin] = aux[srcIdx];
            }
        }
    }

    private void RemoveOceanWater(Action<string> log)
    {
        const byte WaterTmId = 11;
        var waterSet = new HashSet<(int, int, int)>();

        foreach (var (key, blocks) in _chunkMap)
            for (int i = 0; i < CS3; i++)
            {
                if (blocks[i] != WaterTmId) continue;
                int lx = i % CS, ly = i / (CS * CS), lz = (i / CS) % CS;
                waterSet.Add((key.Item1 + lx, key.Item2 + ly, key.Item3 + lz));
            }

        if (waterSet.Count == 0) return;
        log($"  Total water blocks: {waterSet.Count:N0}");

        var visited = new HashSet<(int, int, int)>();
        var toRemove = new List<(int, int, int)>();

        foreach (var start in waterSet)
        {
            if (visited.Contains(start)) continue;
            var component = new List<(int, int, int)> { start };
            visited.Add(start);
            var queue = new Queue<(int, int, int)>();
            queue.Enqueue(start);

            while (queue.Count > 0)
            {
                var (x, y, z) = queue.Dequeue();
                foreach (var nb in new[] { (x+1,y,z),(x-1,y,z),(x,y+1,z),(x,y-1,z),(x,y,z+1),(x,y,z-1) })
                {
                    if (!visited.Contains(nb) && waterSet.Contains(nb))
                    {
                        visited.Add(nb);
                        component.Add(nb);
                        queue.Enqueue(nb);
                    }
                }
            }

            if (component.Count >= OceanThreshold)
                toRemove.AddRange(component);
        }

        log($"  Removing {toRemove.Count:N0} ocean blocks, keeping {waterSet.Count - toRemove.Count:N0}");

        foreach (var (gx, gy, gz) in toRemove)
        {
            var key = (TmRegion.ChunkBase(gx), (gy / CS) * CS, TmRegion.ChunkBase(gz));
            if (_chunkMap.TryGetValue(key, out var blk))
            {
                int lin = TmRegion.LinearIndex(gx - key.Item1, gy - key.Item2, gz - key.Item3);
                if (lin >= 0 && lin < CS3) blk[lin] = 0;
            }
        }
    }

    private void WriteRegions(string saveDir, (int x, int y, int z) regionSize, Action<string> log)
    {
        var regions = new Dictionary<(int, int, int), List<((int, int, int) offset, byte[] blocks, ushort[]? aux)>>();

        foreach (var (key, blocks) in _chunkMap)
        {
            var ro = TmRegion.GetRegionOffset(key, regionSize);
            if (!regions.TryGetValue(ro, out var list))
            {
                list = new();
                regions[ro] = list;
            }
            _auxMap.TryGetValue(key, out var auxData);
            list.Add(((key.Item1 - ro.x, key.Item2 - ro.y, key.Item3 - ro.z), blocks, auxData));
        }

        log($"  Regions to write: {regions.Count:N0}");
        int done = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        foreach (var kvp in regions)
        {
            var ro = kvp.Key;
            int rh = TmRegion.RegionHash(ro.Item1, ro.Item2, ro.Item3, regionSize.x, regionSize.y, regionSize.z);
            var finalChunks = kvp.Value.Select(c =>
                (c.offset, c.blocks, (byte[]?)null, c.aux, TmRegion.ChunkFlagsDefault)).ToList();
            TmRegion.WriteRegionFile(Path.Combine(saveDir, $"{rh}.reg"), ro, regionSize, finalChunks);
            done++;
            if (done % 50 == 0 || done == regions.Count)
                log($"  [{done}/{regions.Count}] {sw.Elapsed.TotalSeconds:F0}s");
        }

        log($"  All {regions.Count} regions written in {sw.Elapsed.TotalSeconds:F1}s");
    }

    private static uint ReadBigEndianUInt32(byte[] buf, int offset)
    {
        return (uint)((buf[offset] << 24) | (buf[offset + 1] << 16) | (buf[offset + 2] << 8) | buf[offset + 3]);
    }
}