using fs_png;
using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

public class MainForm : Form
{
    private TextBox txtPNGPath;
    private Button btnOpen;
    private TextBox txtMaxPngSize;
    private Button btnMount;

    public MainForm()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        BackColor = Color.FromArgb(100, 149, 237);
        ClientSize = new Size(444, 140);
        ForeColor = Color.FromArgb(8, 8, 8);
        MaximizeBox = false;
        Icon = fs_png.Properties.Resources.fs_png;
        Text = "PNG-Based Secret File System";
        MaximumSize = Size;
        MinimumSize = Size;
        Font commonFont = new Font("Segoe UI", 11);
        Font = commonFont;

        Label lblPNGFile = new Label();
        lblPNGFile.Text = "PNGファイル:";
        lblPNGFile.AutoSize = true;
        lblPNGFile.Font = commonFont;
        lblPNGFile.Location = new Point(20, 23);
        Controls.Add(lblPNGFile);

        txtPNGPath = new TextBox();
        txtPNGPath.Font = commonFont;
        txtPNGPath.Location = new Point(lblPNGFile.Right + 5, 20);
        txtPNGPath.Size = new Size(250, 20);
        txtPNGPath.Text = Directory.GetCurrentDirectory() + "\\default.png";
        Controls.Add(txtPNGPath);

        btnOpen = new Button();
        btnOpen.Text = "開く";
        btnOpen.Font = commonFont;
        btnOpen.Location = new Point(txtPNGPath.Right + 5, 18);
        btnOpen.AutoSize = true;
        btnOpen.Click += BtnOpen_Click;
        Controls.Add(btnOpen);

        Label lblMaxDiskSize = new Label();
        lblMaxDiskSize.Text = "最大容量:";
        lblMaxDiskSize.AutoSize = true;
        lblMaxDiskSize.Font = commonFont;
        lblMaxDiskSize.Location = new Point(20, 63);
        Controls.Add(lblMaxDiskSize);

        txtMaxPngSize = new TextBox();
        txtMaxPngSize.Font = commonFont;
        txtMaxPngSize.Location = new Point(lblMaxDiskSize.Right + 5, 60);
        txtMaxPngSize.Size = new Size(100, 25);
        txtMaxPngSize.Text = "2048";
        Controls.Add(txtMaxPngSize);

        Label lblMB = new Label();
        lblMB.Text = "MB";
        lblMB.AutoSize = true;
        lblMB.Font = commonFont;
        lblMB.Location = new Point(txtMaxPngSize.Right + 5, 63);
        Controls.Add(lblMB);

        btnMount = new Button();
        btnMount.Text = "マウント";
        btnMount.Font = commonFont;
        btnMount.AutoSize = true;
        btnMount.Click += BtnMount_Click;
        btnMount.Location = new Point((ClientSize.Width - btnMount.PreferredSize.Width) / 2, 100);
        Controls.Add(btnMount);
    }


    private void BtnOpen_Click(object sender, EventArgs e)
    {
        using (OpenFileDialog ofd = new OpenFileDialog())
        {
            ofd.Filter = "PNGファイル (*.png)|*.png|すべてのファイル (*.*)|*.*";
            if (ofd.ShowDialog() == DialogResult.OK)
            {
                txtPNGPath.Text = ofd.FileName;
            }
        }
    }

    private void BtnMount_Click(object sender, EventArgs e)
    {
        string pngPath = txtPNGPath.Text;
        if (!long.TryParse(txtMaxPngSize.Text, out long maxSizeMB))
        {
            MessageBox.Show("無効な最大PNGサイズです。");
            return;
        }
        long maxPngSize = maxSizeMB * 1024 * 1024;

        // 0なら無制限、マイナスなら規定値(2GB)(マイナスの容量って何だ？？/dev/null的な感じ？？)
        if (maxPngSize < 0)
            maxPngSize = 2 * 1024 * 1024;
        else if (maxPngSize == 0)
            maxPngSize = long.MaxValue;

        Thread mountThread = new Thread(() => Program.MainKernel(pngPath, maxPngSize));
        mountThread.SetApartmentState(ApartmentState.STA);
        mountThread.Start();

        Close();
    }
}
