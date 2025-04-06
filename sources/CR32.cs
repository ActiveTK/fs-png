public class CRC32
{
    private const uint Polynomial = 0xEDB88320;
    private readonly uint[] table = new uint[256];

    public CRC32()
    {
        for (uint i = 0; i < 256; i++)
        {
            uint crc = i;
            for (int j = 0; j < 8; j++)
                crc = ((crc & 1) != 0) ? (Polynomial ^ (crc >> 1)) : (crc >> 1);
            table[i] = crc;
        }
    }

    public uint Calc(byte[] buf)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in buf)
            crc = table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }

    public uint Update(uint crc, byte[] buf, int count)
    {
        for (int i = 0; i < count; i++)
            crc = table[(crc ^ buf[i]) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}