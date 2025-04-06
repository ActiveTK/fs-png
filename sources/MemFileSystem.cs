#region Virtual FileSystem Classes

using DokanNet;
using System.Collections.Generic;
using System.IO;
using System.Security.AccessControl;
using System.Text.Json;
using System.Text.RegularExpressions;
using System;

public class MemoryFileSystem : IDokanOperations
{
    public VirtualDirectory Root { get; set; } = new VirtualDirectory("root");
    public Dictionary<int, long> FileMapping { get; set; } = new Dictionary<int, long>();
    public string PngFilePath { get; set; }
    public PNGHandler PngHandler { get; set; }
    public long MaxPNGSize { get; set; }
    private readonly object syncLock = new object();
    private bool isDirty = false;
    private System.Threading.Timer flushTimer;

    public MemoryFileSystem(string pngFilePath)
    {
        PngFilePath = pngFilePath;
        Logger.Log(Logger.LogType.INFO, $"[MemoryFileSystem] 初期化: PNGファイルパス = {pngFilePath}");
        flushTimer = new System.Threading.Timer(FlushCallback, null, 10000, 10000);

        AppDomain.CurrentDomain.ProcessExit += (sender, e) =>
        {
            Logger.Log(Logger.LogType.INFO, "[ProcessExit] プロセス終了イベント発生。最終保存処理を開始します。");
            DisposeTimer();
            lock (this)
            {
                PngHandler.RebuildPng(Root);
            }
            Logger.Log(Logger.LogType.INFO, "[ProcessExit] 最終保存処理完了。");
        };

    }

    private void FlushCallback(object state)
    {
        lock (syncLock)
        {
            if (isDirty)
            {
                Logger.Log(Logger.LogType.INFO, "[FlushCallback] 変更検出。PNGファイルへ書き出し開始");
                PngHandler.RebuildPng(Root);
                isDirty = false;
                Logger.Log(Logger.LogType.INFO, "[FlushCallback] 書き出し完了");
            }
        }
        GC.Collect();
    }

    public void DisposeTimer() => flushTimer?.Dispose();

    private string[] SplitPath(string fileName)
    {
        return fileName.Trim('\\').Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
    }

    private bool TryGetFile(string fileName, out VirtualFile file)
    {
        file = null;
        var parts = SplitPath(fileName);
        if (parts.Length == 0)
            return false;
        VirtualDirectory current = Root;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (!current.Directories.TryGetValue(parts[i], out VirtualDirectory sub))
                return false;
            current = sub;
        }
        return current.Files.TryGetValue(parts[parts.Length - 1], out file);
    }

    #region IDokanOperations実装
    public NtStatus Mounted(string mountPoint, IDokanFileInfo info)
    {
        Logger.Log(Logger.LogType.INFO, $"[Mounted] マウント完了: {mountPoint}");
        return NtStatus.Success;
    }

    public NtStatus CreateFile(string fileName, DokanNet.FileAccess access, FileShare share, FileMode mode,
        FileOptions options, FileAttributes attributes, IDokanFileInfo info)
    {
        Logger.Log(Logger.LogType.INFO, $"[CreateFile] fileName='{fileName}', mode={mode}, attributes={attributes}, options={options}, IsDirectory={info.IsDirectory}");
        if (fileName == "\\")
        {
            info.IsDirectory = true;
            info.Context = Root;
            return NtStatus.Success;
        }
        bool ContainsInvalidChars(string name)
        {
            char[] invalidChars = new char[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };
            return name.IndexOfAny(invalidChars) >= 0;
        }
        lock (syncLock)
        {
            var parts = SplitPath(fileName);
            VirtualDirectory parent = Root;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (ContainsInvalidChars(parts[i]))
                {
                    Logger.Log(Logger.LogType.ERROR, $"[CreateFile] エラー: 中間ディレクトリ名 '{parts[i]}' に無効な文字が含まれています");
                    return NtStatus.ObjectNameInvalid;
                }
                if (!parent.Directories.TryGetValue(parts[i], out VirtualDirectory sub))
                {
                    if (parent.Files.ContainsKey(parts[i]))
                    {
                        Logger.Log(Logger.LogType.ERROR, $"[CreateFile] エラー: '{parts[i]}' は既にファイルとして存在");
                        return NtStatus.ObjectNameCollision;
                    }
                    sub = new VirtualDirectory(parts[i]);
                    parent.Directories.Add(parts[i], sub);
                    Logger.Log(Logger.LogType.INFO, $"[CreateFile] 中間ディレクトリ自動生成: '{parts[i]}'");
                }
                parent = sub;
            }
            string name = parts[parts.Length - 1];
            if (ContainsInvalidChars(name))
            {
                Logger.Log(Logger.LogType.ERROR, $"[CreateFile] エラー: エントリ名 '{name}' に無効な文字が含まれています");
                return NtStatus.ObjectNameInvalid;
            }
            if (mode == FileMode.CreateNew)
            {
                if (parent.Directories.ContainsKey(name) || parent.Files.ContainsKey(name))
                {
                    Logger.Log(Logger.LogType.ERROR, $"[CreateFile] エラー: '{name}' は既に存在します");
                    return NtStatus.ObjectNameCollision;
                }
                if (info.IsDirectory)
                {
                    var newDir = new VirtualDirectory(name);
                    parent.Directories.Add(name, newDir);
                    Logger.Log(Logger.LogType.INFO, $"[CreateFile] ディレクトリ新規作成: '{name}'");
                    info.Context = newDir;
                    info.IsDirectory = true;
                }
                else
                {
                    var newFile = new VirtualFile(name);
                    parent.Files.Add(name, newFile);
                    Logger.Log(Logger.LogType.INFO, $"[CreateFile] ファイル新規作成: '{name}'");
                    info.Context = newFile;
                }
                UpdateFsMT();
                MarkDirtyAndUpdatePng();
                return NtStatus.Success;
            }
            else if (mode == FileMode.Open)
            {
                if (parent.Files.TryGetValue(name, out VirtualFile targetFile))
                {
                    info.Context = targetFile;
                    return NtStatus.Success;
                }
                else if (parent.Directories.TryGetValue(name, out VirtualDirectory targetDir))
                {
                    info.Context = targetDir;
                    info.IsDirectory = true;
                    return NtStatus.Success;
                }
                else
                {
                    Logger.Log(Logger.LogType.ERROR, $"[CreateFile] エラー: オブジェクト '{name}' が見つかりません");
                    return NtStatus.ObjectNameNotFound;
                }
            }
            else if (mode == FileMode.Create || mode == FileMode.Truncate)
            {
                if (!parent.Files.ContainsKey(name))
                {
                    var newFile = new VirtualFile(name);
                    parent.Files.Add(name, newFile);
                    Logger.Log(Logger.LogType.INFO, $"[CreateFile] ファイル作成: '{name}'");
                }
                else if (mode == FileMode.Truncate)
                {
                    parent.Files[name].MemoryStream.SetLength(0);
                    if (parent.Files[name].TempFilePath != null)
                    {
                        File.Delete(parent.Files[name].TempFilePath);
                        parent.Files[name].TempFilePath = null;
                    }
                    Logger.Log(Logger.LogType.INFO, $"[CreateFile] ファイルトランケート: '{name}'");
                }
                info.Context = parent.Files[name];
                UpdateFsMT();
                MarkDirtyAndUpdatePng();
                return NtStatus.Success;
            }
            return NtStatus.NotImplemented;
        }
    }

    public void Cleanup(string fileName, IDokanFileInfo info)
    {
        Logger.Log(Logger.LogType.INFO, $"[Cleanup] fileName='{fileName}'");
        if (info.DeleteOnClose && !info.IsDirectory)
        {
            lock (syncLock)
            {
                var parts = SplitPath(fileName);
                if (parts.Length == 0)
                    return;
                VirtualDirectory parent = Root;
                for (int i = 0; i < parts.Length - 1; i++)
                {
                    if (!parent.Directories.TryGetValue(parts[i], out VirtualDirectory sub))
                    {
                        Logger.Log(Logger.LogType.ERROR, $"[Cleanup] エラー: ディレクトリ '{parts[i]}' が見つかりません。");
                        return;
                    }
                    parent = sub;
                }
                string name = parts[parts.Length - 1];
                if (parent.Files.ContainsKey(name))
                {
                    parent.Files.Remove(name);
                    Logger.Log(Logger.LogType.INFO, $"[Cleanup] ファイル '{name}' を削除");
                    UpdateFsMT();
                    MarkDirtyAndUpdatePng();
                }
            }
        }
    }

    public void CloseFile(string fileName, IDokanFileInfo info)
    {
        Logger.Log(Logger.LogType.INFO, $"[CloseFile] fileName='{fileName}'");
        info.Context = null;
    }

    public NtStatus ReadFile(string fileName, byte[] buffer, out int bytesRead, long offset, IDokanFileInfo info)
    {
        bytesRead = 0;
        VirtualFile file = info.Context as VirtualFile;
        if (file == null && !TryGetFile(fileName, out file))
        {
            Logger.Log(Logger.LogType.ERROR, $"[ReadFile] エラー: ファイル'{fileName}'が見つかりません");
            return NtStatus.ObjectNameNotFound;
        }
        lock (syncLock)
        {
            if (offset <= file.ActualSize)
            {
                bytesRead = file.Read(buffer, 0, buffer.Length, offset);
                Logger.Log(Logger.LogType.INFO, $"[ReadFile] file='{file.Name}', offset={offset}, 読み込みバイト数={bytesRead}, 実サイズ={file.ActualSize}");
            }
            else
            {
                Logger.Log(Logger.LogType.WARN, $"[ReadFile] オフセット({offset})が実サイズ({file.ActualSize})を超えています");
            }
        }
        return NtStatus.Success;
    }

    public NtStatus WriteFile(string fileName, byte[] buffer, out int bytesWritten, long offset, IDokanFileInfo info)
    {
        bytesWritten = 0;
        VirtualFile file = info.Context as VirtualFile;
        if (file == null && !TryGetFile(fileName, out file))
        {
            Logger.Log(Logger.LogType.ERROR, $"[WriteFile] エラー: ファイル'{fileName}'が見つかりません");
            return NtStatus.ObjectNameNotFound;
        }
        lock (syncLock)
        {
            file.Write(buffer, 0, buffer.Length, offset);
            bytesWritten = buffer.Length;
            file.LastWriteTime = DateTime.Now;
            Logger.Log(Logger.LogType.INFO, $"[WriteFile] file='{file.Name}', offset={offset}, 書き込みバイト数={bytesWritten}, 実サイズ={file.ActualSize}");
        }
        UpdateFsMT();
        MarkDirtyAndUpdatePng();
        return NtStatus.Success;
    }

    public NtStatus FlushFileBuffers(string fileName, IDokanFileInfo info)
    {
        Logger.Log(Logger.LogType.INFO, $"[FlushFileBuffers] fileName='{fileName}'");
        return NtStatus.Success;
    }

    public NtStatus GetFileInformation(string fileName, out FileInformation fileInfo, IDokanFileInfo info)
    {
        fileInfo = new FileInformation();
        if (fileName == "\\")
        {
            fileInfo.Attributes = FileAttributes.Directory;
            fileInfo.FileName = "";
            return NtStatus.Success;
        }
        var parts = SplitPath(fileName);
        VirtualDirectory parent = Root;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (!parent.Directories.TryGetValue(parts[i], out VirtualDirectory sub))
            {
                Logger.Log(Logger.LogType.ERROR, $"[GetFileInformation] エラー: ディレクトリ'{parts[i]}'が見つかりません。");
                return NtStatus.ObjectNameNotFound;
            }
            parent = sub;
        }
        string name = parts[parts.Length - 1];
        if (parent.Files.TryGetValue(name, out VirtualFile file))
        {
            fileInfo.Attributes = file.Attributes;
            fileInfo.CreationTime = file.CreationTime;
            fileInfo.LastAccessTime = file.LastAccessTime;
            fileInfo.LastWriteTime = file.LastWriteTime;
            fileInfo.Length = file.ActualSize;
            fileInfo.FileName = file.Name;
            Logger.Log(Logger.LogType.INFO, $"[GetFileInformation] ファイル '{file.Name}' 情報: 実サイズ={file.ActualSize}, 作成日時={file.CreationTime}");
            return NtStatus.Success;
        }
        else if (parent.Directories.TryGetValue(name, out VirtualDirectory dir))
        {
            fileInfo.Attributes = dir.Attributes;
            fileInfo.CreationTime = dir.CreationTime;
            fileInfo.LastAccessTime = dir.LastAccessTime;
            fileInfo.LastWriteTime = dir.LastWriteTime;
            fileInfo.Length = 0;
            fileInfo.FileName = dir.Name;
            Logger.Log(Logger.LogType.INFO, $"[GetFileInformation] ディレクトリ '{dir.Name}' 情報: サブディレクトリ数={dir.Directories.Count}, ファイル数={dir.Files.Count}");
            return NtStatus.Success;
        }
        Logger.Log(Logger.LogType.ERROR, $"[GetFileInformation] エラー: '{fileName}' が見つかりません。");
        return NtStatus.ObjectNameNotFound;
    }

    public NtStatus FindFiles(string fileName, out IList<FileInformation> files, IDokanFileInfo info)
    {
        files = new List<FileInformation>();
        VirtualDirectory dir;
        if (fileName == "\\")
            dir = Root;
        else
        {
            var parts = SplitPath(fileName);
            dir = Root;
            foreach (var part in parts)
            {
                if (!dir.Directories.TryGetValue(part, out VirtualDirectory sub))
                {
                    Logger.Log(Logger.LogType.ERROR, $"[FindFiles] エラー: ディレクトリ '{part}' が存在しない");
                    return NtStatus.ObjectNameNotFound;
                }
                dir = sub;
            }
        }
        foreach (var d in dir.Directories.Values)
        {
            files.Add(new FileInformation
            {
                FileName = d.Name,
                Attributes = d.Attributes,
                CreationTime = d.CreationTime,
                LastAccessTime = d.LastAccessTime,
                LastWriteTime = d.LastWriteTime,
                Length = 0
            });
        }
        foreach (var f in dir.Files.Values)
        {
            files.Add(new FileInformation
            {
                FileName = f.Name,
                Attributes = f.Attributes,
                CreationTime = f.CreationTime,
                LastAccessTime = f.LastAccessTime,
                LastWriteTime = f.LastWriteTime,
                Length = f.ActualSize
            });
        }
        Logger.Log(Logger.LogType.INFO, $"[FindFiles] ディレクトリ '{dir.Name}' 内: サブディレクトリ数={dir.Directories.Count}, ファイル数={dir.Files.Count}");
        return NtStatus.Success;
    }

    public NtStatus FindFilesWithPattern(string fileName, string searchPattern, out IList<FileInformation> files, IDokanFileInfo info)
    {
        var status = FindFiles(fileName, out files, info);
        if (status != NtStatus.Success)
            return status;
        var regexPattern = "^" + Regex.Escape(searchPattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        var regex = new Regex(regexPattern, RegexOptions.IgnoreCase);
        var filtered = new List<FileInformation>();
        foreach (var fi in files)
        {
            if (regex.IsMatch(fi.FileName))
                filtered.Add(fi);
        }
        files = filtered;
        Logger.Log(Logger.LogType.INFO, $"[FindFilesWithPattern] フィルタ後のエントリ数={files.Count}");
        return NtStatus.Success;
    }

    public NtStatus SetFileAttributes(string fileName, FileAttributes attributes, IDokanFileInfo info)
    {
        Logger.Log(Logger.LogType.INFO, $"[SetFileAttributes] fileName='{fileName}', attributes={attributes}");
        return NtStatus.Success;
    }

    public NtStatus SetFileTime(string fileName, DateTime? creationTime, DateTime? lastAccessTime, DateTime? lastWriteTime, IDokanFileInfo info)
    {
        Logger.Log(Logger.LogType.INFO, $"[SetFileTime] fileName='{fileName}', creationTime={creationTime}, lastAccessTime={lastAccessTime}, lastWriteTime={lastWriteTime}");
        return NtStatus.Success;
    }

    public NtStatus DeleteFile(string fileName, IDokanFileInfo info)
    {
        lock (syncLock)
        {
            var parts = SplitPath(fileName);
            if (parts.Length == 0)
                return NtStatus.AccessDenied;
            VirtualDirectory parent = Root;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (!parent.Directories.TryGetValue(parts[i], out VirtualDirectory sub))
                    return NtStatus.ObjectNameNotFound;
                parent = sub;
            }
            string name = parts[parts.Length - 1];
            if (parent.Files.ContainsKey(name))
            {
                parent.Files.Remove(name);
                Logger.Log(Logger.LogType.INFO, $"[DeleteFile] ファイル '{name}' 削除");
                UpdateFsMT();
                MarkDirtyAndUpdatePng();
                return NtStatus.Success;
            }
            Logger.Log(Logger.LogType.ERROR, $"[DeleteFile] エラー: ファイル '{name}' が見つかりません");
            return NtStatus.ObjectNameNotFound;
        }
    }

    public NtStatus DeleteDirectory(string fileName, IDokanFileInfo info)
    {
        lock (syncLock)
        {
            var parts = SplitPath(fileName);
            if (parts.Length == 0)
                return NtStatus.AccessDenied;
            VirtualDirectory parent = Root;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (!parent.Directories.TryGetValue(parts[i], out VirtualDirectory sub))
                    return NtStatus.ObjectNameNotFound;
                parent = sub;
            }
            string name = parts[parts.Length - 1];
            if (parent.Directories.TryGetValue(name, out VirtualDirectory dir))
            {
                if (dir.Directories.Count == 0 && dir.Files.Count == 0)
                {
                    parent.Directories.Remove(name);
                    Logger.Log(Logger.LogType.INFO, $"[DeleteDirectory] 空ディレクトリ '{name}' 削除");
                    UpdateFsMT();
                    MarkDirtyAndUpdatePng();
                    return NtStatus.Success;
                }
                Logger.Log(Logger.LogType.ERROR, $"[DeleteDirectory] エラー: ディレクトリ '{name}' は空でない (サブディレクトリ数={dir.Directories.Count}, ファイル数={dir.Files.Count})");
                return NtStatus.DirectoryNotEmpty;
            }
            Logger.Log(Logger.LogType.ERROR, $"[DeleteDirectory] エラー: ディレクトリ '{name}' が見つかりません");
            return NtStatus.ObjectNameNotFound;
        }
    }

    public NtStatus MoveFile(string oldName, string newName, bool replace, IDokanFileInfo info)
    {
        Logger.Log(Logger.LogType.DEBUG, $"[MoveFile] 入力パラメータ: oldName='{oldName}', newName='{newName}', replace={replace}");
        lock (syncLock)
        {
            var oldParts = SplitPath(oldName);
            var newParts = SplitPath(newName);
            Logger.Log(Logger.LogType.DEBUG, $"[MoveFile] oldParts=[{string.Join(", ", oldParts)}], newParts=[{string.Join(", ", newParts)}]");
            if (oldParts.Length == 0 || newParts.Length == 0)
            {
                Logger.Log(Logger.LogType.ERROR, "[MoveFile] エラー: oldParts または newParts が空");
                return NtStatus.AccessDenied;
            }
            VirtualDirectory oldParent = Root;
            for (int i = 0; i < oldParts.Length - 1; i++)
            {
                if (!oldParent.Directories.TryGetValue(oldParts[i], out VirtualDirectory sub))
                {
                    Logger.Log(Logger.LogType.ERROR, $"[MoveFile] エラー: 古いパス側のディレクトリ '{oldParts[i]}' が見つかりません");
                    return NtStatus.ObjectNameNotFound;
                }
                oldParent = sub;
            }
            string oldEntryName = oldParts[oldParts.Length - 1];
            Logger.Log(Logger.LogType.DEBUG, $"[MoveFile] 古いパス側の親ディレクトリ='{oldParent.Name}', エントリ名='{oldEntryName}'");
            VirtualDirectory newParent = Root;
            for (int i = 0; i < newParts.Length - 1; i++)
            {
                if (!newParent.Directories.TryGetValue(newParts[i], out VirtualDirectory sub))
                {
                    Logger.Log(Logger.LogType.ERROR, $"[MoveFile] エラー: 新しいパス側のディレクトリ '{newParts[i]}' が見つかりません");
                    return NtStatus.ObjectNameNotFound;
                }
                newParent = sub;
            }
            string newEntryName = newParts[newParts.Length - 1];
            Logger.Log(Logger.LogType.DEBUG, $"[MoveFile] 新しいパス側の親ディレクトリ='{newParent.Name}', エントリ名='{newEntryName}'");
            if (oldParent.Directories.TryGetValue(oldEntryName, out VirtualDirectory dir))
            {
                Logger.Log(Logger.LogType.DEBUG, $"[MoveFile] 対象はディレクトリ。古いエントリ='{oldEntryName}', 新しいエントリ='{newEntryName}'");
                if (newParent.Directories.ContainsKey(newEntryName) || newParent.Files.ContainsKey(newEntryName))
                {
                    Logger.Log(Logger.LogType.DEBUG, $"[MoveFile] 新しいエントリ '{newEntryName}' は既に存在。replace={replace}");
                    if (replace)
                    {
                        newParent.Directories.Remove(newEntryName);
                        Logger.Log(Logger.LogType.DEBUG, $"[MoveFile] 既存のディレクトリエントリ '{newEntryName}' を削除しました");
                    }
                    else
                    {
                        Logger.Log(Logger.LogType.ERROR, $"[MoveFile] エラー: 新しいエントリ '{newEntryName}' は既に存在し、置換が許可されていません");
                        return NtStatus.ObjectNameCollision;
                    }
                }
                oldParent.Directories.Remove(oldEntryName);
                dir.Name = newEntryName;
                newParent.Directories[newEntryName] = dir;
                Logger.Log(Logger.LogType.DEBUG, $"[MoveFile] ディレクトリ '{oldEntryName}' を '{newEntryName}' にリネームしました");
                UpdateFsMT();
                MarkDirtyAndUpdatePng();
                Logger.Log(Logger.LogType.DEBUG, "[MoveFile] 処理結果: NtStatus.Success (ディレクトリ移動)");
                return NtStatus.Success;
            }
            else if (oldParent.Files.TryGetValue(oldEntryName, out VirtualFile file))
            {
                Logger.Log(Logger.LogType.DEBUG, $"[MoveFile] 対象はファイル。古いエントリ='{oldEntryName}', 新しいエントリ='{newEntryName}', 実サイズ={file.ActualSize}");
                if (newParent.Files.ContainsKey(newEntryName) || newParent.Directories.ContainsKey(newEntryName))
                {
                    Logger.Log(Logger.LogType.DEBUG, $"[MoveFile] 新しいエントリ '{newEntryName}' は既に存在。replace={replace}");
                    if (replace)
                    {
                        newParent.Files.Remove(newEntryName);
                        Logger.Log(Logger.LogType.DEBUG, $"[MoveFile] 既存のファイルエントリ '{newEntryName}' を削除しました");
                    }
                    else
                    {
                        Logger.Log(Logger.LogType.ERROR, $"[MoveFile] エラー: 新しいエントリ '{newEntryName}' は既に存在し、置換が許可されていません");
                        return NtStatus.ObjectNameCollision;
                    }
                }
                oldParent.Files.Remove(oldEntryName);
                file.Name = newEntryName;
                newParent.Files[newEntryName] = file;
                Logger.Log(Logger.LogType.DEBUG, $"[MoveFile] ファイル '{oldEntryName}' を '{newEntryName}' にリネームしました");
                UpdateFsMT();
                MarkDirtyAndUpdatePng();
                Logger.Log(Logger.LogType.DEBUG, "[MoveFile] 処理結果: NtStatus.Success (ファイル移動)");
                return NtStatus.Success;
            }
            else
            {
                Logger.Log(Logger.LogType.ERROR, $"[MoveFile] エラー: 古いエントリ '{oldEntryName}' が存在しません");
                return NtStatus.ObjectNameNotFound;
            }
        }
    }

    public NtStatus SetEndOfFile(string fileName, long length, IDokanFileInfo info)
    {
        lock (syncLock)
        {
            VirtualFile file = info.Context as VirtualFile;
            if (file == null && !TryGetFile(fileName, out file))
                return NtStatus.ObjectNameNotFound;
            file.ActualSize = length;
            Logger.Log(Logger.LogType.INFO, $"[SetEndOfFile] file='{file.Name}', 新サイズ={file.ActualSize}");
            UpdateFsMT();
            MarkDirtyAndUpdatePng();
            return NtStatus.Success;
        }
    }

    public NtStatus SetAllocationSize(string fileName, long length, IDokanFileInfo info)
    {
        return SetEndOfFile(fileName, length, info);
    }

    public NtStatus LockFile(string fileName, long offset, long length, IDokanFileInfo info)
    {
        Logger.Log(Logger.LogType.INFO, $"[LockFile] fileName='{fileName}', offset={offset}, length={length}");
        return NtStatus.Success;
    }

    public NtStatus UnlockFile(string fileName, long offset, long length, IDokanFileInfo info)
    {
        Logger.Log(Logger.LogType.INFO, $"[UnlockFile] fileName='{fileName}', offset={offset}, length={length}");
        return NtStatus.Success;
    }

    public NtStatus GetDiskFreeSpace(out long freeBytesAvailable, out long totalNumberOfBytes, out long totalNumberOfFreeBytes, IDokanFileInfo info)
    {
        var totalUsed = PngHandler.GetTotalUsage();
        freeBytesAvailable = MaxPNGSize - totalUsed;
        totalNumberOfFreeBytes = MaxPNGSize - totalUsed;
        totalNumberOfBytes = MaxPNGSize;
        return NtStatus.Success;
    }

    public NtStatus GetVolumeInformation(out string volumeLabel, out FileSystemFeatures features, out string fileSystemName, out uint maximumComponentLength, IDokanFileInfo info)
    {
        volumeLabel = "fs-png";
        fileSystemName = "NTFS";
        maximumComponentLength = 255;
        features = FileSystemFeatures.CasePreservedNames | FileSystemFeatures.CaseSensitiveSearch | FileSystemFeatures.PersistentAcls;
        Logger.Log(Logger.LogType.INFO, $"[GetVolumeInformation] volumeLabel='{volumeLabel}', fileSystemName='{fileSystemName}', maxComponentLength={maximumComponentLength}");
        return NtStatus.Success;
    }

    public NtStatus GetFileSecurity(string fileName, out FileSystemSecurity security, AccessControlSections sections, IDokanFileInfo info)
    {
        Logger.Log(Logger.LogType.INFO, $"[GetFileSecurity] fileName='{fileName}'");
        security = null;
        return NtStatus.NotImplemented;
    }

    public NtStatus SetFileSecurity(string fileName, FileSystemSecurity security, AccessControlSections sections, IDokanFileInfo info)
    {
        Logger.Log(Logger.LogType.INFO, $"[SetFileSecurity] fileName='{fileName}'");
        return NtStatus.NotImplemented;
    }

    public NtStatus Unmounted(IDokanFileInfo info)
    {
        Logger.Log(Logger.LogType.INFO, "[Unmounted] マウント解除完了");
        lock (syncLock)
        {
            Logger.Log(Logger.LogType.INFO, "[Unmounted] 最終書き出し開始");
            UpdateFsMT();
            PngHandler.RebuildPng(Root);
            Logger.Log(Logger.LogType.INFO, "[Unmounted] 最終書き出し完了");
        }
        flushTimer.Dispose();
        return NtStatus.Success;
    }

    public NtStatus EnumerateNamedStreams(string fileName, IntPtr enumContext, out string streamName, out long streamSize, IDokanFileInfo info)
    {
        Logger.Log(Logger.LogType.INFO, $"[EnumerateNamedStreams] fileName='{fileName}'");
        streamName = string.Empty;
        streamSize = 0;
        return NtStatus.NotImplemented;
    }

    public NtStatus FindStreams(string fileName, out IList<FileInformation> streams, IDokanFileInfo info)
    {
        Logger.Log(Logger.LogType.INFO, $"[FindStreams] fileName='{fileName}'");
        streams = new List<FileInformation>();
        return NtStatus.NotImplemented;
    }
    #endregion

    #region PNG更新処理（fsMT/fsDFの再構築）

    private void UpdateFsMT()
    {
        Logger.Log(Logger.LogType.INFO, $"[UpdateFsMT] ルート: サブディレクトリ数={Root.Directories.Count}, ファイル数={Root.Files.Count}");
        if (PngHandler == null)
            return;
        DirectoryEntry de = ConvertDirectory(Root);
        PngHandler.SetFsMTData(de);
        Logger.Log(Logger.LogType.INFO, "[UpdateFsMT] fsMT用 JSON:");
        Logger.Log(Logger.LogType.INFO, JsonSerializer.Serialize(de));
    }

    private void MarkDirtyAndUpdatePng()
    {
        if (PngHandler == null)
            return;
        MarkDirty();
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
        {
            de.Contents.Add(ConvertDirectory(sub));
        }
        return de;
    }

    private void MarkDirty()
    {
        lock (syncLock)
        {
            isDirty = true;
        }
    }
    #endregion
}

#endregion