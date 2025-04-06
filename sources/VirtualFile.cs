using System.IO;
using System;

public class VirtualFile
{
    public string Name { get; set; }
    public MemoryStream MemoryStream { get; set; }
    public string TempFilePath { get; set; }
    public long ActualSize { get; set; }
    public FileAttributes Attributes { get; set; }
    public DateTime CreationTime { get; set; }
    public DateTime LastAccessTime { get; set; }
    public DateTime LastWriteTime { get; set; }
    public int FileNumber { get; set; } = 0;

    // 仮想ファイルは、最初の100MBはメモリ上に保持し、それ以降は一時ファイルに書き出して管理する
    public const long MemoryThreshold = 100L * 1024 * 1024;

    public VirtualFile(string name)
    {
        Name = name;
        MemoryStream = new MemoryStream();
        TempFilePath = null;
        ActualSize = 0;
        Attributes = FileAttributes.Normal;
        CreationTime = DateTime.Now;
        LastAccessTime = DateTime.Now;
        LastWriteTime = DateTime.Now;
    }

    public void Write(byte[] buffer, int offsetInBuffer, int count, long fileOffset)
    {
        long endOffset = fileOffset + count;
        if (endOffset > ActualSize)
            ActualSize = endOffset;

        // 書き込み先がメモリ領域にかかる場合
        if (fileOffset < MemoryThreshold)
        {
            long memWriteEnd = Math.Min(MemoryThreshold, fileOffset + count);
            int memCount = (int)(memWriteEnd - fileOffset);
            MemoryStream.Position = fileOffset;
            MemoryStream.Write(buffer, offsetInBuffer, memCount);

            if (count > memCount)
            {
                WriteToTemp(buffer, offsetInBuffer + memCount, count - memCount, fileOffset + memCount - MemoryThreshold);
            }
        }
        else
        {
            WriteToTemp(buffer, offsetInBuffer, count, fileOffset - MemoryThreshold);
        }
    }

    private void WriteToTemp(byte[] buffer, int offsetInBuffer, int count, long tempOffset)
    {
        if (TempFilePath == null)
        {
            TempFilePath = Path.GetTempFileName();
        }
        using (var fsTemp = new FileStream(TempFilePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read))
        {
            fsTemp.Position = tempOffset;
            fsTemp.Write(buffer, offsetInBuffer, count);
        }
    }

    public int Read(byte[] buffer, int offsetInBuffer, int count, long fileOffset)
    {
        int totalRead = 0;
        if (fileOffset < MemoryThreshold)
        {
            MemoryStream.Position = fileOffset;
            int toRead = (int)Math.Min(count, MemoryThreshold - fileOffset);
            int readMem = MemoryStream.Read(buffer, offsetInBuffer, toRead);
            totalRead += readMem;
            if (totalRead < count && ActualSize > MemoryThreshold && TempFilePath != null)
            {
                int remaining = count - totalRead;
                int readTemp = ReadFromTemp(buffer, offsetInBuffer + totalRead, remaining, fileOffset + totalRead - MemoryThreshold);
                totalRead += readTemp;
            }
        }
        else
        {
            if (TempFilePath != null)
            {
                int readTemp = ReadFromTemp(buffer, offsetInBuffer, count, fileOffset - MemoryThreshold);
                totalRead += readTemp;
            }
        }
        return totalRead;
    }

    private int ReadFromTemp(byte[] buffer, int offsetInBuffer, int count, long tempOffset)
    {
        using (var fsTemp = new FileStream(TempFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            fsTemp.Position = tempOffset;
            return fsTemp.Read(buffer, offsetInBuffer, count);
        }
    }
}