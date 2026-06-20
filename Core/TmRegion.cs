namespace McToTm.Core;

public static class TmRegion
{
    public const int ChunkSize = 32;
    public const int ChunkLength = ChunkSize * ChunkSize * ChunkSize;
    public const uint ChunkFlagsDefault = 0x5085;

    public static int Point3dHash(int x, int y, int z)
    {
        return ((x & 0x3FF) << 20) | ((z & 0x3FF) << 10) | (y & 0x3FF);
    }

    public static int RegionHash(int offsetX, int offsetY, int offsetZ, int regionSizeX, int regionSizeY, int regionSizeZ)
    {
        int h = 0;
        int px = offsetX, py = offsetY, pz = offsetZ;

        int num2 = py / regionSizeY;

        int num3;
        if (px < 0)
        {
            int px2 = px + 1;
            num3 = (-px2) / regionSizeX + 1;
            h |= 0x40000000;
        }
        else
        {
            num3 = px / regionSizeX;
        }

        int num4;
        if (pz < 0)
        {
            int pz2 = pz + 1;
            num4 = (-pz2) / regionSizeZ + 1;
            h |= 0x20000000;
        }
        else
        {
            num4 = pz / regionSizeZ;
        }

        h += (num3 & 0x7FF) << 18;
        h += (num4 & 0x7FF) << 7;
        h += num2 & 0x7F;
        return h;
    }

    public static (int x, int y, int z) GetRegionOffset((int x, int y, int z) globalPos, (int x, int y, int z) regionSize)
    {
        int rx = globalPos.x >= 0
            ? (globalPos.x / regionSize.x) * regionSize.x
            : -(((-globalPos.x - 1) / regionSize.x + 1) * regionSize.x);
        int ry = (globalPos.y / regionSize.y) * regionSize.y;
        int rz = globalPos.z >= 0
            ? (globalPos.z / regionSize.z) * regionSize.z
            : -(((-globalPos.z - 1) / regionSize.z + 1) * regionSize.z);
        return (rx, ry, rz);
    }

    public static byte[] RleCompressBytes(byte[] cache)
    {
        if (cache.Length == 0) return Array.Empty<byte>();
        var stream = new List<byte>(cache.Length / 2);
        byte prev = cache[0];
        int count = 0;
        for (int i = 1; i < cache.Length; i++)
        {
            byte cur = cache[i];
            if (count == 255 || cur != prev)
            {
                stream.Add((byte)count);
                stream.Add(prev);
                prev = cur;
                count = 0;
            }
            else
            {
                count++;
            }
        }
        stream.Add((byte)count);
        stream.Add(prev);

        int length = stream.Count;
        while (length > 3 && stream[length - 3] == stream[length - 1])
        {
            length -= 2;
            stream[length - 2] = 0;
        }
        var result = new byte[length];
        for (int i = 0; i < length; i++) result[i] = stream[i];
        return result;
    }

    public static byte[] RleCompressUint16(ushort[] cache)
    {
        if (cache.Length == 0) return Array.Empty<byte>();
        var stream = new List<ushort>(cache.Length / 2);
        ushort prev = cache[0];
        int count = 0;
        for (int i = 1; i < cache.Length; i++)
        {
            ushort cur = cache[i];
            if (count == 255 || cur != prev)
            {
                stream.Add((ushort)count);
                stream.Add(prev);
                prev = cur;
                count = 0;
            }
            else
            {
                count++;
            }
        }
        stream.Add((ushort)count);
        stream.Add(prev);

        int length = stream.Count;
        while (length > 3 && stream[length - 3] == stream[length - 1])
        {
            length -= 2;
            stream[length - 2] = 0;
        }
        var result = new byte[length * 2];
        for (int i = 0; i < length; i++)
        {
            BitConverter.TryWriteBytes(result.AsSpan(i * 2), stream[i]);
        }
        return result;
    }

    public static byte[] RleDecompressBytes(byte[] data, int totalLength)
    {
        var output = new byte[totalLength];
        int pos = 0;
        int i = 0;
        byte lastVal = 0;
        while (i + 1 < data.Length)
        {
            int count = data[i] + 1;
            byte val = data[i + 1];
            lastVal = val;
            for (int c = 0; c < count && pos < totalLength; c++)
                output[pos++] = val;
            i += 2;
        }
        while (pos < totalLength)
            output[pos++] = lastVal;
        return output;
    }

    public static ushort[] RleDecompressUint16(byte[] data, int totalLength)
    {
        var output = new ushort[totalLength];
        int pos = 0;
        ushort lastVal = 0;
        int i = 0;
        while (i + 3 < data.Length)
        {
            int count = BitConverter.ToUInt16(data, i) + 1;
            ushort val = BitConverter.ToUInt16(data, i + 2);
            lastVal = val;
            int end = Math.Min(pos + count, totalLength);
            for (int c = pos; c < end; c++)
                output[c] = val;
            pos = end;
            i += 4;
        }
        while (pos < totalLength)
            output[pos++] = lastVal;
        return output;
    }

    public static void WriteRegionFile(string path, (int x, int y, int z) regOffset,
        (int x, int y, int z) regionSize, List<((int x, int y, int z) offset, byte[] blocks, byte[]? light, ushort[]? aux, uint flags)> chunks)
    {
        int rh = RegionHash(regOffset.x, regOffset.y, regOffset.z, regionSize.x, regionSize.y, regionSize.z);
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        w.Write(rh);
        w.Write(regOffset.x);
        w.Write(regOffset.y);
        w.Write(regOffset.z);
        w.Write(chunks.Count);

        foreach (var chunk in chunks)
        {
            int ch = Point3dHash(chunk.offset.x, chunk.offset.y, chunk.offset.z);
            w.Write(ch);
            w.Write(chunk.flags);
            w.Write((long)0);

            byte[] blockRle = RleCompressBytes(chunk.blocks);
            w.Write(blockRle.Length);
            if (blockRle.Length > 0) w.Write(blockRle);

            byte[] lightData = chunk.light ?? DefaultLight();
            byte[] lightRle = RleCompressBytes(lightData);
            w.Write(lightRle.Length);
            if (lightRle.Length > 0) w.Write(lightRle);

            ushort[] auxData = chunk.aux ?? new ushort[ChunkLength];
            byte[] auxRle = RleCompressUint16(auxData);
            int auxCount = auxRle.Length / 2;
            w.Write(auxCount);
            if (auxRle.Length > 0) w.Write(auxRle);
        }

        File.WriteAllBytes(path, ms.ToArray());
    }

    public static RegionData ReadRegionFile(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        using var r = new BinaryReader(new MemoryStream(data));

        int rh = r.ReadInt32();
        int ox = r.ReadInt32();
        int oy = r.ReadInt32();
        int oz = r.ReadInt32();
        int chunkCount = r.ReadInt32();

        var chunks = new List<ChunkData>();
        for (int c = 0; c < chunkCount; c++)
        {
            int ch = r.ReadInt32();
            uint flags = r.ReadUInt32();
            r.ReadInt64();

            int blockSize = r.ReadInt32();
            byte[] blockRle = blockSize > 0 ? r.ReadBytes(blockSize) : Array.Empty<byte>();

            int lightSize = r.ReadInt32();
            byte[] lightRle = lightSize > 0 ? r.ReadBytes(lightSize) : Array.Empty<byte>();

            int auxCount = r.ReadInt32();
            byte[] auxRle = auxCount > 0 ? r.ReadBytes(auxCount * 2) : Array.Empty<byte>();

            byte[] blocks = RleDecompressBytes(blockRle, ChunkLength);
            byte[] light = RleDecompressBytes(lightRle, ChunkLength);
            ushort[] aux = RleDecompressUint16(auxRle, ChunkLength);

            int cx = (ch >> 20) & 0x3FF;
            int cz = (ch >> 10) & 0x3FF;
            int cy = ch & 0x3FF;

            chunks.Add(new ChunkData
            {
                Hash = ch,
                Offset = (cx, cy, cz),
                Flags = flags,
                Blocks = blocks,
                Light = light,
                Aux = aux,
            });
        }

        return new RegionData
        {
            Hash = rh,
            Offset = (ox, oy, oz),
            Chunks = chunks,
        };
    }

    public static HeaderData ParseHeaderDat(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        using var r = new BinaryReader(new MemoryStream(data));

        int version = r.ReadInt32();
        uint exeVersion = r.ReadUInt32();

        int createdVersion = 0;
        if (version > 35)
            createdVersion = r.ReadInt32();

        string ReadString()
        {
            int length = 0, shift = 0;
            while (true)
            {
                byte b = r.ReadByte();
                length |= (b & 0x7F) << shift;
                shift += 7;
                if (b < 0x80) break;
            }
            return System.Text.Encoding.UTF8.GetString(r.ReadBytes(length));
        }

        string mapName = ReadString();
        string owner = ReadString();
        long dateCreated = r.ReadInt64();

        if (version > 134 && version < 211)
            r.ReadByte();
        if (version > 208)
            r.ReadInt64();
        else
        {
            if (version > 39)
                r.ReadInt32();
        }

        bool isAutoSave = false;
        if (version > 31)
            isAutoSave = r.ReadBoolean();

        var regionSize = (x: 512, y: 512, z: 512);
        var maxMapSize = (x: 1024, y: 512, z: 1024);
        var mapBoundMin = (x: 0, y: 0, z: 0);
        var mapBoundMax = (x: 1024, y: 512, z: 1024);

        if (version > 54)
        {
            if (version < 304)
                r.ReadBytes(12);
            maxMapSize = (r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
            if (version > 216)
            {
                mapBoundMin = (r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
                mapBoundMax = (r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
            }
            else
            {
                mapBoundMin = (0, 0, 0);
                mapBoundMax = maxMapSize;
            }
            regionSize = (r.ReadUInt16(), r.ReadUInt16(), r.ReadUInt16());
            r.ReadBytes(6);
        }

        return new HeaderData
        {
            Version = version,
            MapName = mapName,
            Owner = owner,
            RegionSize = regionSize,
            MapBoundMin = mapBoundMin,
            MapBoundMax = mapBoundMax,
            RawData = data,
        };
    }

    public static void TouchHeaderDat(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        using var r = new BinaryReader(new MemoryStream(data));

        int version = r.ReadInt32();
        r.ReadInt32();
        if (version > 35) r.ReadInt32();

        for (int s = 0; s < 2; s++)
        {
            int length = 0, shift = 0;
            while (true)
            {
                byte b = r.ReadByte();
                length |= (b & 0x7F) << shift;
                shift += 7;
                if (b < 0x80) break;
            }
            r.ReadBytes(length);
        }

        r.ReadInt64();

        if (version > 134 && version < 211)
            r.ReadByte();

        if (version > 208)
        {
            int offset = (int)r.BaseStream.Position;
            var epoch = new DateTime(1, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            long ticks = (DateTime.UtcNow - epoch).Ticks;
            BitConverter.TryWriteBytes(data.AsSpan(offset), ticks);
            File.WriteAllBytes(path, data);
        }
    }

    private static byte[] DefaultLight()
    {
        var light = new byte[ChunkLength];
        Array.Fill(light, (byte)0xF0);
        return light;
    }

    public static int ChunkBase(int v)
    {
        return v >= 0 ? (v / ChunkSize) * ChunkSize : -(((-v - 1) / ChunkSize + 1) * ChunkSize);
    }

    public static int LinearIndex(int lx, int ly, int lz)
    {
        return lx + lz * ChunkSize + ly * ChunkSize * ChunkSize;
    }

    public static int BitLength(int value)
    {
        if (value <= 0) return 1;
        int bits = 0;
        while (value > 0) { bits++; value >>= 1; }
        return bits;
    }

    public static int[] UnpackBitArray(long[] dataLongs, int numEntries, int paletteSize)
    {
        if (paletteSize <= 1) return new int[numEntries];
        int bits = Math.Max(4, BitLength(Math.Max(paletteSize, 2) - 1));
        ulong mask = (1UL << bits) - 1;

        var longs = new ulong[dataLongs.Length];
        Buffer.BlockCopy(dataLongs, 0, longs, 0, dataLongs.Length * 8);
        int n = longs.Length;
        int vplong = 64 / bits;
        int nPerlong = (numEntries + vplong - 1) / vplong;

        var indices = new int[numEntries];

        if (n == nPerlong)
        {
            for (int i = 0; i < numEntries; i++)
            {
                int longIdx = i / vplong;
                int bitOff = (i % vplong) * bits;
                if (longIdx < n)
                    indices[i] = (int)((longs[longIdx] >> bitOff) & mask);
            }
        }
        else
        {
            for (int i = 0; i < numEntries; i++)
            {
                long bitPos = (long)i * bits;
                int arrIdx = (int)(bitPos / 64);
                int bitOff = (int)(bitPos % 64);
                if (arrIdx >= n) break;
                ulong val = (longs[arrIdx] >> bitOff) & mask;
                if (bitOff + bits > 64 && arrIdx + 1 < n)
                    val |= (longs[arrIdx + 1] << (64 - bitOff)) & mask;
                indices[i] = (int)val;
            }
        }
        return indices;
    }

    public static int[] UnpackDenseBitArray(long[] dataLongs, int bits, int total)
    {
        var result = new int[total];
        ulong mask = (1UL << bits) - 1;
        var ulongs = new ulong[dataLongs.Length];
        Buffer.BlockCopy(dataLongs, 0, ulongs, 0, dataLongs.Length * 8);
        int n = ulongs.Length;

        for (int i = 0; i < total; i++)
        {
            long bitPos = (long)i * bits;
            int arrIdx = (int)(bitPos / 64);
            int bitOff = (int)(bitPos % 64);
            if (arrIdx >= n) break;
            ulong val = (ulongs[arrIdx] >> bitOff) & mask;
            if (bitOff + bits > 64 && arrIdx + 1 < n)
                val |= (ulongs[arrIdx + 1] << (64 - bitOff)) & mask;
            result[i] = (int)val;
        }
        return result;
    }
}

public class HeaderData
{
    public int Version { get; set; }
    public string MapName { get; set; } = "";
    public string Owner { get; set; } = "";
    public (int x, int y, int z) RegionSize { get; set; }
    public (int x, int y, int z) MapBoundMin { get; set; }
    public (int x, int y, int z) MapBoundMax { get; set; }
    public byte[] RawData { get; set; } = Array.Empty<byte>();
}

public class RegionData
{
    public int Hash { get; set; }
    public (int x, int y, int z) Offset { get; set; }
    public List<ChunkData> Chunks { get; set; } = new();
}

public class ChunkData
{
    public int Hash { get; set; }
    public (int x, int y, int z) Offset { get; set; }
    public uint Flags { get; set; }
    public byte[] Blocks { get; set; } = Array.Empty<byte>();
    public byte[] Light { get; set; } = Array.Empty<byte>();
    public ushort[] Aux { get; set; } = Array.Empty<ushort>();
}