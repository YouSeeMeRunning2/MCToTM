using McToTm.Core;
using McToTm.Formats;

namespace McToTm.Converters;

public class SchematicConverter
{
    private const int CS = TmRegion.ChunkSize;
    private const int CS3 = CS * CS * CS;

    public void Convert(string schematicPath, string saveDir, (int x, int y, int z) origin, Action<string>? log = null)
    {
        log ??= Console.WriteLine;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var header = TmRegion.ParseHeaderDat(Path.Combine(saveDir, "header.dat"));
        log($"Save: {header.MapName} (version {header.Version})");
        log($"  Region size: ({header.RegionSize.x},{header.RegionSize.y},{header.RegionSize.z})");

        log($"\nLoading: {schematicPath}");
        var grid = SchematicReader.LoadSchematicGrid(schematicPath);
        int sx = grid.GetLength(0), sy = grid.GetLength(1), sz = grid.GetLength(2);

        int placed = CountNonZero(grid);
        log($"  Dimensions: {sx} x {sy} x {sz}, non-air: {placed:N0}");
        if (placed == 0) { log("No blocks to inject."); return; }

        log($"\nPlacing at origin: ({origin.x}, {origin.y}, {origin.z})");
        var chunks = GridToChunks(grid, origin);
        log($"  Total chunks: {chunks.Count}");

        var regionGroups = GroupByRegion(chunks, header.RegionSize);
        log($"  Total regions: {regionGroups.Count}");

        int regCount = 0;
        foreach (var (regOffset, chunkList) in regionGroups)
        {
            int rh = TmRegion.RegionHash(regOffset.x, regOffset.y, regOffset.z,
                header.RegionSize.x, header.RegionSize.y, header.RegionSize.z);
            string regPath = Path.Combine(saveDir, $"{rh}.reg");

            var merged = LoadExistingChunks(regPath);

            foreach (var (localOffset, blockData) in chunkList)
            {
                int ch = TmRegion.Point3dHash(localOffset.x, localOffset.y, localOffset.z);
                if (merged.TryGetValue(ch, out var old))
                {
                    for (int i = 0; i < CS3; i++)
                        if (blockData[i] != 0) old.blocks[i] = blockData[i];
                    merged[ch] = old;
                }
                else
                {
                    var light = new byte[CS3];
                    Array.Fill(light, (byte)0xF0);
                    merged[ch] = (blockData, light, new ushort[CS3], TmRegion.ChunkFlagsDefault, localOffset);
                }
            }

            var finalChunks = merged.Values
                .Select(c => (c.offset, c.blocks, (byte[]?)c.light, (ushort[]?)c.aux, c.flags))
                .ToList();

            TmRegion.WriteRegionFile(regPath, regOffset, header.RegionSize, finalChunks);
            regCount++;
            int total = finalChunks.Sum(c => c.blocks.Count(b => b != 0));
            log($"  Wrote {Path.GetFileName(regPath)} ({finalChunks.Count} chunks, {total:N0} blocks)");
        }

        TmHash.RehashHeaderDat(Path.Combine(saveDir, "header.dat"), header.Version);
        log($"\nDone in {sw.Elapsed.TotalSeconds:F1}s - {regCount} region file(s) written");
    }

    private static Dictionary<int, (byte[] blocks, byte[] light, ushort[] aux, uint flags, (int, int, int) offset)> LoadExistingChunks(string regPath)
    {
        var result = new Dictionary<int, (byte[] blocks, byte[] light, ushort[] aux, uint flags, (int, int, int) offset)>();
        if (!File.Exists(regPath)) return result;
        try
        {
            var existing = TmRegion.ReadRegionFile(regPath);
            foreach (var c in existing.Chunks)
                result[c.Hash] = (c.Blocks, c.Light, c.Aux, c.Flags, c.Offset);
        }
        catch { }
        return result;
    }

    private static Dictionary<(int, int, int), byte[]> GridToChunks(byte[,,] grid, (int x, int y, int z) origin)
    {
        int sx = grid.GetLength(0), sy = grid.GetLength(1), sz = grid.GetLength(2);
        var chunks = new Dictionary<(int, int, int), byte[]>();

        for (int gx = 0; gx < sx; gx++)
        for (int gy = 0; gy < sy; gy++)
        for (int gz = 0; gz < sz; gz++)
        {
            byte bid = grid[gx, gy, gz];
            if (bid == 0) continue;

            int wx = gx + origin.x, wy = gy + origin.y, wz = gz + origin.z;
            int cgx = TmRegion.ChunkBase(wx);
            int cgy = (wy / CS) * CS;
            int cgz = TmRegion.ChunkBase(wz);

            var key = (cgx, cgy, cgz);
            if (!chunks.TryGetValue(key, out var chunk))
            {
                chunk = new byte[CS3];
                chunks[key] = chunk;
            }
            chunk[TmRegion.LinearIndex(wx - cgx, wy - cgy, wz - cgz)] = bid;
        }

        return chunks;
    }

    private static Dictionary<(int x, int y, int z), List<((int x, int y, int z) offset, byte[] blocks)>>
        GroupByRegion(Dictionary<(int, int, int), byte[]> chunks, (int x, int y, int z) regionSize)
    {
        var regions = new Dictionary<(int x, int y, int z), List<((int x, int y, int z), byte[])>>();
        foreach (var (key, data) in chunks)
        {
            var ro = TmRegion.GetRegionOffset(key, regionSize);
            if (!regions.TryGetValue(ro, out var list))
            {
                list = new();
                regions[ro] = list;
            }
            list.Add(((key.Item1 - ro.x, key.Item2 - ro.y, key.Item3 - ro.z), data));
        }
        return regions;
    }

    private static int CountNonZero(byte[,,] grid)
    {
        int count = 0;
        int sx = grid.GetLength(0), sy = grid.GetLength(1), sz = grid.GetLength(2);
        for (int x = 0; x < sx; x++)
        for (int y = 0; y < sy; y++)
        for (int z = 0; z < sz; z++)
            if (grid[x, y, z] != 0) count++;
        return count;
    }
}