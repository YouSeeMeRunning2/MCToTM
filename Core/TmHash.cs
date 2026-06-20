namespace McToTm.Core;

public static class TmHash
{
    public static byte[] BuildMessageHash(byte[] data, int length, int version)
    {
        var hashArr = new byte[64];
        if (length > data.Length) length = data.Length;

        for (int i = 0; i < length; i++)
        {
            int idx = i % 64;
            hashArr[idx] = (byte)((hashArr[idx] + data[i]) & 0xFF);
        }

        int num4, num5, num6;
        if (version < 130) { num4 = 1; num5 = 2000; num6 = 1000; }
        else if (version < 197) { num4 = 13; num5 = 3591; num6 = 1901; }
        else { num4 = 17; num5 = 1849; num6 = 1656; }

        long num3 = 0;
        for (int j = 0; j < 64; j++)
            num3 += (long)(hashArr[j] + num4) * num5;
        num3 /= num6;

        byte b2 = (byte)(num3 & 0xFF);
        for (int k = 0; k < 64; k++)
            hashArr[k] = (byte)((hashArr[k] + b2) & 0xFF);

        return hashArr;
    }

    private static void ScramblePairSwap(byte[] data)
    {
        for (int i = 0; i < data.Length - 1; i += 2)
            (data[i], data[i + 1]) = (data[i + 1], data[i]);
    }

    private static void ScrambleOppositeEndsSwap(byte[] data)
    {
        int n = 0, m = data.Length - 1;
        while (n < data.Length / 2)
        {
            (data[n], data[m]) = (data[m], data[n]);
            n++; m--;
        }
    }

    private static void Scramble4thBitSwap(byte[] data)
    {
        for (int i = 0; i < data.Length - 1; i++)
        {
            byte b = data[i];
            if ((b & 8) > 0)
                b = (b & 4) > 0 ? (byte)(b & 0xFB) : (byte)(b | 4);
            data[i] = b;
        }
    }

    private static void Scramble6thBitSwap(byte[] data)
    {
        for (int i = 0; i < data.Length - 1; i++)
        {
            byte b = data[i];
            if ((b & 0x20) > 0)
                b = (b & 0x10) > 0 ? (byte)(b & 0xEF) : (byte)(b | 0x10);
            data[i] = b;
        }
    }

    public static int RandomScramble(byte[] data)
    {
        int scrambleId = Random.Shared.Next(0, 4);
        switch (scrambleId)
        {
            case 1:
                Scramble4thBitSwap(data);
                ScrambleOppositeEndsSwap(data);
                ScramblePairSwap(data);
                break;
            case 2:
                Scramble6thBitSwap(data);
                ScrambleOppositeEndsSwap(data);
                ScramblePairSwap(data);
                break;
            case 3:
                Scramble6thBitSwap(data);
                ScramblePairSwap(data);
                ScrambleOppositeEndsSwap(data);
                break;
            default:
                Scramble4thBitSwap(data);
                ScramblePairSwap(data);
                ScrambleOppositeEndsSwap(data);
                break;
        }
        return scrambleId;
    }

    public static byte[] WriteWithHash(byte[] payload, int version)
    {
        byte[] hashBytes = BuildMessageHash(payload, payload.Length, version);
        int scrambleId = RandomScramble(hashBytes);

        var result = new byte[payload.Length + hashBytes.Length + 1 + 4];
        Buffer.BlockCopy(payload, 0, result, 0, payload.Length);
        Buffer.BlockCopy(hashBytes, 0, result, payload.Length, hashBytes.Length);
        result[payload.Length + hashBytes.Length] = (byte)scrambleId;
        BitConverter.TryWriteBytes(result.AsSpan(payload.Length + hashBytes.Length + 1), hashBytes.Length);
        return result;
    }

    public static void RehashHeaderDat(string path, int version)
    {
        byte[] data = File.ReadAllBytes(path);
        int hashLen = BitConverter.ToInt32(data, data.Length - 4);
        if (hashLen < 1 || hashLen > 64) return;

        int payloadLen = data.Length - 5 - hashLen;
        byte[] payload = new byte[payloadLen];
        Buffer.BlockCopy(data, 0, payload, 0, payloadLen);

        byte[] newData = WriteWithHash(payload, version);
        File.WriteAllBytes(path, newData);
    }
}