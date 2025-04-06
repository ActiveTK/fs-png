using fs_png;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using FileAccess = System.IO.FileAccess;

public class PNGHandler : IDisposable
{
    private string filePath;
    private FileStream fs;
    private readonly byte[] pngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    private List<PngChunk> originalChunks = new List<PngChunk>();
    private CRC32 crcCalculator = new CRC32();
    private DirectoryEntry fsMTData;
    private const int ChunkMemoryThreshold = 100 * 1024 * 1024;

    public PNGHandler(string path)
    {
        filePath = path;
        Logger.Log(Logger.LogType.INFO, $"[PNGHandler] 初期化: filePath='{filePath}'");
        if (!File.Exists(filePath))
            throw new Win32Exception("File Not Found");
        fs = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        if (!CheckPngSignature())
            throw new Win32Exception("File Type Error");
        ReadAllChunks();
    }
    public void RebuildPng(VirtualDirectory root)
    {
        Logger.Log(Logger.LogType.INFO, "[RebuildPng] PNG再構築開始");
        string tempFilePath = filePath + ".tmp";
        using (FileStream tempStream = new FileStream(tempFilePath, FileMode.Create, System.IO.FileAccess.ReadWrite))
        {
            tempStream.Write(pngSignature, 0, pngSignature.Length);

            int iendIndex = originalChunks.FindIndex(c => c.Type == "IEND");
            if (iendIndex < 0)
            {
                Logger.Log(Logger.LogType.WARN, "[RebuildPng] IENDチャンクが見つかりませんため自動生成します");
                byte[] iendTypeBytes = Encoding.ASCII.GetBytes("IEND");
                uint iendCrc = crcCalculator.Calc(iendTypeBytes);
                PngChunk iendChunkData = new PngChunk
                {
                    Length = 0,
                    Type = "IEND",
                    Data = new byte[0],
                    TempFilePath = null,
                    Crc = iendCrc
                };
                originalChunks.Add(iendChunkData);
                iendIndex = originalChunks.Count - 1;
            }

            for (int i = 0; i < iendIndex; i++)
            {
                PngChunk chunk = originalChunks[i];
                if (chunk.Type == "fsMT" || chunk.Type == "fsDF")
                    continue;
                WriteRawChunk(tempStream, chunk);
            }
            ReassignAllFileNumbers(root);
            DirectoryEntry de = fsMTData ?? ConvertDirectory(root);
            WriteFsMTChunk(tempStream, de);
            AddFsDFChunks(tempStream, root);
            PngChunk iendChunk = originalChunks[iendIndex];
            WriteRawChunk(tempStream, iendChunk);
            tempStream.Flush();
        }
        fs.Dispose();
        File.Replace(tempFilePath, filePath, null);
        fs = new FileStream(filePath, FileMode.Open, System.IO.FileAccess.ReadWrite, FileShare.Read);
        fs.Position = 0;
        ReadAllChunks();
        Logger.Log(Logger.LogType.INFO, "[RebuildPng] PNG再構築完了。新バイナリサイズ=" + fs.Length);
    }

    private void ReassignAllFileNumbers(VirtualDirectory root)
    {
        int counter = 1;
        ReassignFileNumbersRecursive(root, ref counter);
    }

    private void ReassignFileNumbersRecursive(VirtualDirectory dir, ref int counter)
    {
        foreach (var file in dir.Files.Values)
        {
            file.FileNumber = counter++;
            Logger.Log(Logger.LogType.INFO, $"[ReassignFileNumbers] ファイル '{file.Name}' に新しい FileNumber={file.FileNumber} を割り当てました");
        }
        foreach (var sub in dir.Directories.Values)
        {
            ReassignFileNumbersRecursive(sub, ref counter);
        }
    }

    private void AddFsDFChunks(Stream s, VirtualDirectory dir)
    {
        foreach (var file in dir.Files.Values)
        {
            WriteFsDFChunkOptimized(s, file.FileNumber, file);
        }
        foreach (var sub in dir.Directories.Values)
        {
            AddFsDFChunks(s, sub);
        }
    }

    private bool CheckPngSignature()
    {
        byte[] buf = new byte[pngSignature.Length];
        fs.Seek(0, SeekOrigin.Begin);
        fs.Read(buf, 0, buf.Length);
        for (int i = 0; i < pngSignature.Length; i++)
        {
            if (buf[i] != pngSignature[i])
                return false;
        }
        Logger.Log(Logger.LogType.INFO, "[CheckPngSignature] 正常なPNGシグネチャ検出");
        return true;
    }

    private void ReadAllChunks()
    {
        Logger.Log(Logger.LogType.INFO, "[ReadAllChunks] PNGチャンク読み込み開始");
        originalChunks.Clear();
        fs.Seek(pngSignature.Length, SeekOrigin.Begin);
        while (fs.Position < fs.Length)
        {
            byte[] lenBytes = new byte[4];
            if (fs.Read(lenBytes, 0, 4) < 4)
                break;
            if (BitConverter.IsLittleEndian)
                Array.Reverse(lenBytes);
            int length = BitConverter.ToInt32(lenBytes, 0);

            byte[] typeBytes = new byte[4];
            if (fs.Read(typeBytes, 0, 4) < 4)
                break;
            string chunkType = Encoding.ASCII.GetString(typeBytes);

            PngChunk chunk = new PngChunk
            {
                Length = length,
                Type = chunkType
            };

            if (length > ChunkMemoryThreshold)
            {
                // チャンクが巨大な場合は一時ファイルに保存
                string tempChunkFile = Path.GetTempFileName();
                using (var tempFs = new FileStream(tempChunkFile, FileMode.Create, System.IO.FileAccess.Write))
                {
                    byte[] buffer = new byte[64 * 1024];
                    int remaining = length;
                    while (remaining > 0)
                    {
                        int toRead = Math.Min(buffer.Length, remaining);
                        int read = fs.Read(buffer, 0, toRead);
                        if (read <= 0)
                            break;
                        tempFs.Write(buffer, 0, read);
                        remaining -= read;
                    }
                }
                chunk.TempFilePath = tempChunkFile;
                chunk.Data = null;
            }
            else
            {
                byte[] data = new byte[length];
                if (fs.Read(data, 0, length) < length)
                    break;
                chunk.Data = data;
                chunk.TempFilePath = null;
            }
            byte[] crcBytes = new byte[4];
            if (fs.Read(crcBytes, 0, 4) < 4)
                break;
            if (BitConverter.IsLittleEndian)
                Array.Reverse(crcBytes);
            chunk.Crc = BitConverter.ToUInt32(crcBytes, 0);

            originalChunks.Add(chunk);
            Logger.Log(Logger.LogType.INFO, $"[ReadAllChunks] 読み込みチャンク: Type='{chunk.Type}', Length={chunk.Length}, CRC=0x{chunk.Crc:X}");
            if (chunk.Type == "IEND")
                break;
        }
        Logger.Log(Logger.LogType.INFO, $"[ReadAllChunks] 総チャンク数={originalChunks.Count}");
    }

    public long GetTotalUsage()
    {
        long totalDefaultChunks = pngSignature.Length;
        int iendIndex = originalChunks.FindIndex(c => c.Type == "IEND");
        if (iendIndex < 0)
        {
            Logger.Log(Logger.LogType.ERROR, "[RebuildPng] IEND チャンクが見つかりませんため中断");
            return 0;
        }
        for (int i = 0; i < iendIndex; i++)
        {
            PngChunk chunk = originalChunks[i];
            if (chunk.Type == "fsMT" || chunk.Type == "fsDF")
                continue;
            totalDefaultChunks += chunk.Length + 12;
        }
        return fs.Length - totalDefaultChunks;
    }

    public void SetFsMTData(DirectoryEntry de)
    {
        fsMTData = de;
        Logger.Log(Logger.LogType.INFO, "[SetFsMTData] fsMT用 DirectoryEntry 設定完了");
    }

    // ファイル番号とファイルデータを合わせて返す。ファイルが大きい時はメモリ部分と一時ファイル部分を連結する(はず)
    private void WriteFsDFChunkOptimized(Stream s, int fileNumber, VirtualFile file)
    {
        int chunkDataLength = 4 + (int)file.ActualSize;
        byte[] lengthBytes = BitConverter.GetBytes(chunkDataLength);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(lengthBytes);
        s.Write(lengthBytes, 0, 4);

        byte[] typeBytes = Encoding.ASCII.GetBytes("fsDF");
        s.Write(typeBytes, 0, 4);

        uint crc = 0xFFFFFFFF;
        crc = crcCalculator.Update(crc, typeBytes, typeBytes.Length);

        byte[] fileNumberBytes = BitConverter.GetBytes(fileNumber);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(fileNumberBytes);
        s.Write(fileNumberBytes, 0, 4);
        crc = crcCalculator.Update(crc, fileNumberBytes, fileNumberBytes.Length);

        file.MemoryStream.Position = 0;
        byte[] buffer = new byte[64 * 1024];
        int bytesRead;
        while ((bytesRead = file.MemoryStream.Read(buffer, 0, buffer.Length)) > 0)
        {
            s.Write(buffer, 0, bytesRead);
            crc = crcCalculator.Update(crc, buffer, bytesRead);
        }
        if (file.TempFilePath != null)
        {
            using (var fsTemp = new FileStream(file.TempFilePath, FileMode.Open, System.IO.FileAccess.Read))
            {
                while ((bytesRead = fsTemp.Read(buffer, 0, buffer.Length)) > 0)
                {
                    s.Write(buffer, 0, bytesRead);
                    crc = crcCalculator.Update(crc, buffer, bytesRead);
                }
            }
        }

        uint finalCrc = crc ^ 0xFFFFFFFF;
        byte[] crcBytes = BitConverter.GetBytes(finalCrc);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(crcBytes);
        s.Write(crcBytes, 0, 4);

        Logger.Log(Logger.LogType.INFO, $"[WriteFsDFChunkOptimized] fsDFチャンク生成: ファイル番号={fileNumber}, 実サイズ={file.ActualSize}");
    }

    private void WriteRawChunk(Stream s, PngChunk chunk)
    {
        byte[] lenBytes = BitConverter.GetBytes(chunk.Length);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(lenBytes);
        s.Write(lenBytes, 0, 4);
        byte[] typeBytes = Encoding.ASCII.GetBytes(chunk.Type);
        s.Write(typeBytes, 0, 4);
        if (chunk.Data != null)
        {
            s.Write(chunk.Data, 0, chunk.Data.Length);
        }
        else if (chunk.TempFilePath != null)
        {
            using (var tempFs = new FileStream(chunk.TempFilePath, FileMode.Open, System.IO.FileAccess.Read))
            {
                byte[] buffer = new byte[64 * 1024];
                int bytesRead;
                while ((bytesRead = tempFs.Read(buffer, 0, buffer.Length)) > 0)
                {
                    s.Write(buffer, 0, bytesRead);
                }
            }
        }
        byte[] crcBytes = BitConverter.GetBytes(chunk.Crc);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(crcBytes);
        s.Write(crcBytes, 0, 4);
        Logger.Log(Logger.LogType.INFO, $"[WriteRawChunk] 元チャンク出力: Type='{chunk.Type}', Length={chunk.Length}, CRC=0x{chunk.Crc:X}");
    }

    private void WriteChunk(Stream s, string chunkType, byte[] data)
    {
        int length = data.Length;
        byte[] lenBytes = BitConverter.GetBytes(length);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(lenBytes);
        s.Write(lenBytes, 0, 4);
        byte[] typeBytes = Encoding.ASCII.GetBytes(chunkType);
        s.Write(typeBytes, 0, 4);
        s.Write(data, 0, data.Length);
        byte[] crcData = new byte[typeBytes.Length + data.Length];
        Buffer.BlockCopy(typeBytes, 0, crcData, 0, typeBytes.Length);
        Buffer.BlockCopy(data, 0, crcData, typeBytes.Length, data.Length);
        uint crcValue = crcCalculator.Calc(crcData);
        byte[] crcBytes = BitConverter.GetBytes(crcValue);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(crcBytes);
        s.Write(crcBytes, 0, 4);
        Logger.Log(Logger.LogType.INFO, $"[WriteChunk] 新規チャンク生成: Type='{chunkType}', Length={length}, CRC=0x{crcValue:X}");
    }

    private void WriteFsMTChunk(Stream s, DirectoryEntry de)
    {
        var options = new JsonSerializerOptions { WriteIndented = false };
        string json = JsonSerializer.Serialize(de, options);
        byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
        byte[] compressed;
        using (var ms = new MemoryStream())
        {
            using (var gzip = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            {
                gzip.Write(jsonBytes, 0, jsonBytes.Length);
            }
            compressed = ms.ToArray();
        }
        WriteChunk(s, "fsMT", compressed);
        Logger.Log(Logger.LogType.INFO, $"[WriteFsMTChunk] fsMTチャンク生成: 元JSONサイズ={jsonBytes.Length}, 圧縮後サイズ={compressed.Length}");
    }

    private DirectoryEntry ConvertDirectory(VirtualDirectory dir)
    {
        var de = new DirectoryEntry
        {
            Type = "Directory",
            DirectoryName = dir.Name,
            DirectoryCreatedAt = dir.CreationTime,
            Contents = new List<DirectoryEntry>()
        };
        foreach (var file in dir.Files.Values)
        {
            de.Contents.Add(new DirectoryEntry
            {
                Type = "File",
                FileName = file.Name,
                FileSize = file.ActualSize,
                FileID = file.FileNumber,
                CreatedAt = file.CreationTime,
                UpdatedAt = file.LastWriteTime
            });
        }
        foreach (var sub in dir.Directories.Values)
            de.Contents.Add(ConvertDirectory(sub));
        return de;
    }
    public void RestoreFileData(VirtualDirectory root)
    {
        Logger.Log(Logger.LogType.INFO, "[RestoreFileData] 復元開始");
        foreach (var chunk in originalChunks)
        {
            if (chunk.Type == "fsDF")
            {
                if (chunk.Data == null && chunk.TempFilePath == null)
                    continue;
                byte[] fileNumBytes = new byte[4];
                if (chunk.Data != null)
                {
                    Array.Copy(chunk.Data, 0, fileNumBytes, 0, 4);
                }
                else
                {
                    using (var tempFs = new FileStream(chunk.TempFilePath, FileMode.Open, System.IO.FileAccess.Read))
                    {
                        tempFs.Read(fileNumBytes, 0, 4);
                    }
                }
                if (BitConverter.IsLittleEndian)
                    Array.Reverse(fileNumBytes);
                int fileNum = BitConverter.ToInt32(fileNumBytes, 0);
                byte[] fileData;
                if (chunk.Data != null)
                {
                    fileData = new byte[chunk.Data.Length - 4];
                    Array.Copy(chunk.Data, 4, fileData, 0, fileData.Length);
                }
                else
                {
                    using (var tempFs = new FileStream(chunk.TempFilePath, FileMode.Open, System.IO.FileAccess.Read))
                    {
                        tempFs.Position = 4;
                        fileData = new byte[tempFs.Length - 4];
                        tempFs.Read(fileData, 0, fileData.Length);
                    }
                }
                VirtualFile vf = FindVirtualFileByNumber(root, fileNum);
                if (vf != null)
                {
                    vf.MemoryStream = new MemoryStream();
                    vf.MemoryStream.Write(fileData, 0, (int)Math.Min(fileData.Length, VirtualFile.MemoryThreshold));
                    if (fileData.Length > VirtualFile.MemoryThreshold)
                    {
                        string tempPath = Path.GetTempFileName();
                        File.WriteAllBytes(tempPath, fileData.AsSpan((int)VirtualFile.MemoryThreshold).ToArray());
                        vf.TempFilePath = tempPath;
                    }
                    vf.ActualSize = fileData.Length;
                    vf.MemoryStream.Position = 0;
                    Logger.Log(Logger.LogType.INFO, $"[RestoreFileData] 復元: '{vf.Name}' (FileNumber={fileNum}), データサイズ={fileData.Length}");
                }
            }
        }
        Logger.Log(Logger.LogType.INFO, "[RestoreFileData] 復元完了");
    }

    private VirtualFile FindVirtualFileByNumber(VirtualDirectory dir, int fileNumber)
    {
        foreach (var file in dir.Files.Values)
            if (file.FileNumber == fileNumber)
                return file;
        foreach (var sub in dir.Directories.Values)
        {
            VirtualFile found = FindVirtualFileByNumber(sub, fileNumber);
            if (found != null)
                return found;
        }
        return null;
    }

    public byte[] GetFsMTChunkData()
    {
        foreach (var chunk in originalChunks)
        {
            if (chunk.Type == "fsMT")
                return chunk.Data;
        }
        return null;
    }
    public void Dispose()
    {
        fs?.Dispose();
        Logger.Log(Logger.LogType.INFO, "[PNGHandler] リソース解放完了");
    }
}
