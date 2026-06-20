using System.IO.Compression;
using fNbt;

namespace McToTm.Formats;

public static class McaReader
{
    public static IEnumerable<(int chunkXLocal, int chunkZLocal, NbtCompound root)> ReadChunks(string mcaPath)
    {
        byte[] header;
        using (var fs = File.OpenRead(mcaPath))
        {
            header = new byte[8192];
            fs.ReadExactly(header);
        }

        for (int i = 0; i < 1024; i++)
        {
            uint entry = (uint)((header[i * 4] << 24) | (header[i * 4 + 1] << 16) |
                                (header[i * 4 + 2] << 8) | header[i * 4 + 3]);
            int offsetSectors = (int)((entry >> 8) & 0xFFFFFF);
            int sectorCount = (int)(entry & 0xFF);
            if (offsetSectors == 0 || sectorCount == 0) continue;

            int chunkXLocal = i % 32;
            int chunkZLocal = i / 32;

            NbtCompound? root = null;
            try
            {
                using var fs = File.OpenRead(mcaPath);
                fs.Seek(offsetSectors * 4096L, SeekOrigin.Begin);
                var lenBuf = new byte[5];
                fs.ReadExactly(lenBuf);
                int length = (lenBuf[0] << 24) | (lenBuf[1] << 16) | (lenBuf[2] << 8) | lenBuf[3];
                int compression = lenBuf[4];
                var data = new byte[length - 1];
                fs.ReadExactly(data);

                byte[] raw;
                if (compression == 1)
                    raw = GZipDecompress(data);
                else if (compression == 2)
                    raw = ZlibDecompress(data);
                else if (compression == 3)
                    raw = data;
                else
                    continue;

                var nbtFile = new NbtFile();
                nbtFile.LoadFromBuffer(raw, 0, raw.Length, NbtCompression.None);
                root = nbtFile.RootTag;
            }
            catch { continue; }

            if (root != null)
                yield return (chunkXLocal, chunkZLocal, root);
        }
    }

    public static long FastInhabitedTime(byte[] raw)
    {
        byte[] needle = System.Text.Encoding.ASCII.GetBytes("\x04\x00\x0dInhabitedTime");
        int idx = FindBytes(raw, needle);
        if (idx < 0) return 0;
        int valStart = idx + needle.Length;
        if (valStart + 8 > raw.Length) return 0;
        return (long)raw[valStart] << 56 | (long)raw[valStart + 1] << 48 |
               (long)raw[valStart + 2] << 40 | (long)raw[valStart + 3] << 32 |
               (long)raw[valStart + 4] << 24 | (long)raw[valStart + 5] << 16 |
               (long)raw[valStart + 6] << 8 | raw[valStart + 7];
    }

    public static byte[] DecompressChunkRaw(string mcaPath, int offsetSectors)
    {
        using var fs = File.OpenRead(mcaPath);
        fs.Seek(offsetSectors * 4096L, SeekOrigin.Begin);
        var lenBuf = new byte[5];
        fs.ReadExactly(lenBuf);
        int length = (lenBuf[0] << 24) | (lenBuf[1] << 16) | (lenBuf[2] << 8) | lenBuf[3];
        int compression = lenBuf[4];
        var data = new byte[length - 1];
        fs.ReadExactly(data);

        if (compression == 2) return ZlibDecompress(data);
        if (compression == 1) return GZipDecompress(data);
        return data;
    }

    private static byte[] ZlibDecompress(byte[] data)
    {
        using var input = new MemoryStream(data, 2, data.Length - 2);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        deflate.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] GZipDecompress(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }

    private static int FindBytes(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }
            if (match) return i;
        }
        return -1;
    }
}