using DokanNet;
using DokanNet.Logging;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace fs_png
{
    #region PNG Handler & Chunk Classes
    public class PngChunk
    {
        public int Length { get; set; }
        public string Type { get; set; }
        public byte[] Data { get; set; }
        public string TempFilePath { get; set; }
        public uint Crc { get; set; }
    }
    #endregion

    #region Main Program
    internal class Program
    {
        [STAThread]
        private static void Main()
        {
            Logger.Init();
            Application.EnableVisualStyles();
            Application.Run(new MainForm());
        }

        static char MountLetter = 'P';
        public static void MainKernel(string PNGPath, long MaxPngSize)
        {
            try
            {
                using (var exitEvent = new ManualResetEventSlim(false))
                using (var dokan = new Dokan(new NullLogger()))
                using (var pngHandler = new PNGHandler(PNGPath))
                {
                    var memFS = new MemoryFileSystem(PNGPath)
                    {
                        PngHandler = pngHandler,
                        MaxPNGSize = MaxPngSize
                    };

                    byte[] fsMTData = pngHandler.GetFsMTChunkData();
                    if (fsMTData != null)
                    {
                        memFS.Root = VirtualDirectoryParser.ParseVirtualDirectoryFromFsMT(fsMTData);
                        Logger.Log(Logger.LogType.INFO, "[Main] fsMTチャンクによりディレクトリツリー再構築完了");
                    }
                    else
                    {
                        Logger.Log(Logger.LogType.INFO, "[Main] fsMTチャンクが存在しないため、空のルートを使用");
                    }
                    pngHandler.RestoreFileData(memFS.Root);

                    Console.CancelKeyPress += (sender, e) =>
                    {
                        e.Cancel = true;
                        exitEvent.Set();
                        Application.Exit();
                    };

                    var trayIcon = new NotifyIcon
                    {
                        Icon = Properties.Resources.fs_png,
                        Text = "fs-png",
                        Visible = true,
                        ContextMenuStrip = CreateContextMenu(() =>
                        {
                            exitEvent.Set();
                            Logger.CleanUp();
                            Application.Exit();
                        })
                    };

                    var usedDrives = DriveInfo.GetDrives()
                                  .Select(d => char.ToUpper(d.Name[0]))
                                  .ToHashSet();
                    if (usedDrives.Contains(MountLetter))
                    {
                        MountLetter = Enumerable.Range('A', 26)
                                                    .Select(i => (char)i)
                                                    .Where(letter => !usedDrives.Contains(letter))
                                                    .OrderBy(letter => letter)
                                                    .FirstOrDefault();
                    }

                    var dokanBuilder = new DokanInstanceBuilder(dokan)
                        .ConfigureOptions(options =>
                        {
                            options.MountPoint = MountLetter + ":\\";
                        });

                    using (var dokanInstance = dokanBuilder.Build(memFS))
                    {
                        Logger.Log(Logger.LogType.INFO, "[Main] 仮想ドライブマウント完了。");
                        Process.Start("explorer.exe", MountLetter + ":\\");
                        Application.Run();
                    }
                    Logger.Log(Logger.LogType.INFO, "[Main] マウント解除。");
                    exitEvent.Wait();
                }
            }
            catch (Exception ex)
            {
                Logger.Log(Logger.LogType.ERROR, "[Main] 予期せぬエラー: " + ex.Message);
                MessageBox.Show("エラーが発生しました: " + ex.Message,
                    "エラー - fs-png",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error,
                    MessageBoxDefaultButton.Button2
                );
            }
        }

        private static ContextMenuStrip CreateContextMenu(Action exitAction)
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("エクスプローラーで表示", null, (s, e) =>
            {
                Process.Start("explorer.exe", MountLetter + ":\\");
            });
            menu.Items.Add("ログファイルを表示", null, (s, e) =>
            {
                Logger.OpenLogFile();
            });
            menu.Items.Add("終了 / アンマウント", null, (s, e) =>
            {
                exitAction();
            });
            return menu;
        }
    }
   
    #endregion
}
