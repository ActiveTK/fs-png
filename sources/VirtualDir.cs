using fs_png;
using System.Collections.Generic;
using System.IO.Compression;
using System.IO;
using System.Text.Json;
using System;
using System.Text.Json.Serialization;
public class VirtualDirectory
{
    public string Name { get; set; }
    public Dictionary<string, VirtualDirectory> Directories { get; set; }
        = new Dictionary<string, VirtualDirectory>(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, VirtualFile> Files { get; set; }
        = new Dictionary<string, VirtualFile>(StringComparer.OrdinalIgnoreCase);
    public FileAttributes Attributes { get; set; }
    public DateTime CreationTime { get; set; }
    public DateTime LastAccessTime { get; set; }
    public DateTime LastWriteTime { get; set; }

    public VirtualDirectory(string name)
    {
        Name = name;
        Attributes = FileAttributes.Directory;
        CreationTime = DateTime.Now;
        LastAccessTime = DateTime.Now;
        LastWriteTime = DateTime.Now;
    }
}

public class DirectoryEntry
{
    public string Type { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string DirectoryName { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? DirectoryCreatedAt { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<DirectoryEntry> Contents { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string FileName { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? FileSize { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? FileID { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? CreatedAt { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? UpdatedAt { get; set; }
}
#region VirtualDirectoryParser Helper
public static class VirtualDirectoryParser
{
    public static VirtualDirectory ParseVirtualDirectoryFromFsMT(byte[] fsMTChunkData)
    {
        Logger.Log(Logger.LogType.INFO, "[VirtualDirectoryParser] fsMTチャンクからパース開始");
        using (var ms = new MemoryStream(fsMTChunkData))
        using (var gzip = new GZipStream(ms, CompressionMode.Decompress))
        using (var decompressed = new MemoryStream())
        {
            gzip.CopyTo(decompressed);
            byte[] jsonBytes = decompressed.ToArray();
            var de = JsonSerializer.Deserialize<DirectoryEntry>(jsonBytes);
            if (de.Contents == null)
                de.Contents = new List<DirectoryEntry>();
            Logger.Log(Logger.LogType.INFO, "[VirtualDirectoryParser] パース完了: " + JsonSerializer.Serialize(de));
            return ConvertDirectoryEntryToVirtualDirectory(de);
        }
    }

    private static VirtualDirectory ConvertDirectoryEntryToVirtualDirectory(DirectoryEntry de)
    {
        VirtualDirectory dir = new VirtualDirectory(de.DirectoryName)
        {
            CreationTime = de.DirectoryCreatedAt ?? DateTime.Now
        };
        if (de.Contents != null)
        {
            foreach (var child in de.Contents)
            {
                if (child.Type == "File")
                {
                    VirtualFile vf = new VirtualFile(child.FileName)
                    {
                        CreationTime = child.CreatedAt ?? DateTime.Now,
                        LastWriteTime = child.UpdatedAt ?? DateTime.Now,
                        FileNumber = child.FileID ?? 0
                    };
                    vf.MemoryStream = new MemoryStream();
                    dir.Files.Add(child.FileName, vf);
                }
                else if (child.Type == "Directory")
                {
                    var subDir = ConvertDirectoryEntryToVirtualDirectory(child);
                    dir.Directories.Add(child.DirectoryName, subDir);
                }
            }
        }
        Logger.Log(Logger.LogType.INFO, $"[ConvertDirectoryEntryToVirtualDirectory] 再構築完了: '{dir.Name}', サブディレクトリ数={dir.Directories.Count}, ファイル数={dir.Files.Count}");
        return dir;
    }
}

#endregion
