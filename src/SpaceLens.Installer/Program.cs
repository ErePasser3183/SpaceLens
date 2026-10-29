using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Linq;

namespace SpaceLens.Installer;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        if (args.Length == 2 && args[0] == "--verify-installer")
            return InstallerChecks.Run(args[1]);
        Application.Run(new InstallWindow());
        return 0;
    }
}

internal static class Installation
{
    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "SpaceLens");

    public static string ValidateDirectory(string input)
    {
        string path = input.Trim();
        // Require a local, fully qualified path. In particular, C:folder is not absolute.
        if (path.Length < 3 || !char.IsLetter(path[0]) || path[1] != ':' || path[2] != '\\' || path.IndexOf('"') >= 0)
            throw new IOException("请选择本机磁盘上的完整安装路径。");
        path = Path.GetFullPath(path).TrimEnd('\\');
        if (path.Length <= 3) throw new IOException("请在磁盘中选择一个文件夹，不能直接安装到磁盘根目录。");
        if (File.Exists(path)) throw new IOException("这个路径是文件，请选择一个文件夹。");
        for (string? parent = path; !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
            if (Directory.Exists(parent) && (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("请选择实际文件夹，不要使用目录链接。");
        if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any() && !IsInstallation(path))
            throw new IOException("这个文件夹里已有其他文件，请选择空文件夹或已有的 SpaceLens 安装目录。");
        return path;
    }

    private static bool IsInstallation(string path)
    {
        try
        {
            if (!File.Exists(Path.Combine(path, "Update.exe")) || File.Exists(Path.Combine(path, ".portable"))) return false;
            var metadata = XDocument.Load(Path.Combine(path, "current", "sq.version"));
            return metadata.Descendants().Any(e => e.Name.LocalName == "id" && e.Value == "SpaceLens");
        }
        catch { return false; }
    }

    public static ProcessStartInfo CreateStartInfo(string payload, string destination) => new ProcessStartInfo
    {
        FileName = payload,
        Arguments = "--silent --installto \"" + destination + "\"",
        UseShellExecute = false,
        CreateNoWindow = true
    };

    public static async Task InstallAsync(string destination, Func<ProcessStartInfo, Task<int>>? runInstaller = null)
    {
        // All target writes happen only after the user clicks Install.
        destination = ValidateDirectory(destination);
        Directory.CreateDirectory(destination);
        string probe = Path.Combine(destination, ".spacelens-write-" + Guid.NewGuid().ToString("N"));
        using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
        string staging = Path.Combine(Path.GetTempPath(), "SpaceLens-Setup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            string payload = Path.Combine(staging, "Setup.exe");
            using (var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("SpaceLens.Setup.exe")
                   ?? throw new IOException("安装包不完整，请重新下载安装程序。"))
            using (var output = new FileStream(payload, FileMode.CreateNew))
                await resource.CopyToAsync(output);
            int exitCode = await (runInstaller ?? RunInstallerAsync)(CreateStartInfo(payload, destination));
            if (exitCode != 0) throw new IOException("安装未完成（错误码 " + exitCode + "），请关闭正在运行的 SpaceLens 后重试。");
            if (!File.Exists(Path.Combine(destination, "current", "SpaceLens.exe")))
                throw new IOException("未找到安装后的程序，请重新安装。");
        }
        finally
        {
            // This private temporary directory is created above; never delete the selected target.
            try { Directory.Delete(staging, true); } catch { }
        }
    }

    private static async Task<int> RunInstallerAsync(ProcessStartInfo start)
    {
        using var process = Process.Start(start) ?? throw new IOException("无法启动安装程序。");
        await Task.Run(() => process.WaitForExit());
        return process.ExitCode;
    }
}

internal sealed class InstallWindow : Form
{
    internal readonly TextBox Destination = new TextBox();
    internal readonly Button Browse = new Button();
    internal readonly Button Install = new Button();
    private readonly Button cancel = new Button();
    private readonly Label status = new Label();
    private readonly ProgressBar progress = new ProgressBar();
    private readonly CheckBox launch = new CheckBox();
    private bool installing;
    private string? installedDirectory;

    public InstallWindow()
    {
        Text = "安装 SpaceLens";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Font = new Font("Microsoft YaHei UI", 10);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(620, 310);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(248, 249, 251);
        Controls.Add(new Label { Text = "SpaceLens", Font = new Font(Font.FontFamily, 22, FontStyle.Bold), AutoSize = true, Location = new Point(26, 22) });
        Controls.Add(new Label { Text = "选择安装位置", AutoSize = true, Location = new Point(28, 86) });
        Destination.SetBounds(30, 116, 452, 30);
        Destination.Text = Installation.DefaultDirectory;
        Browse.Text = "浏览…";
        Browse.SetBounds(492, 114, 96, 34);
        Browse.Click += (_, _) =>
        {
            using var picker = new FolderBrowserDialog { Description = "选择 SpaceLens 的安装文件夹", ShowNewFolderButton = true, SelectedPath = Directory.Exists(Destination.Text) ? Destination.Text : "" };
            if (picker.ShowDialog(this) == DialogResult.OK)
                Destination.Text = string.Equals(Path.GetFileName(picker.SelectedPath), "SpaceLens", StringComparison.OrdinalIgnoreCase)
                    ? picker.SelectedPath : Path.Combine(picker.SelectedPath, "SpaceLens");
        };
        status.SetBounds(30, 162, 558, 48);
        status.Text = "安装后支持后台自动更新。";
        progress.SetBounds(30, 215, 558, 5);
        progress.Visible = false;
        launch.Text = "完成后启动 SpaceLens";
        launch.Checked = true;
        launch.SetBounds(30, 254, 270, 30);
        Install.Text = "安装";
        Install.SetBounds(374, 250, 102, 38);
        Install.BackColor = Color.FromArgb(48, 112, 175);
        Install.ForeColor = Color.White;
        Install.FlatStyle = FlatStyle.Flat;
        Install.Click += async (_, _) => await InstallClickedAsync();
        cancel.Text = "取消";
        cancel.SetBounds(486, 250, 102, 38);
        cancel.Click += (_, _) => Close();
        Controls.AddRange(new Control[] { Destination, Browse, status, progress, launch, Install, cancel });
        AcceptButton = Install;
        CancelButton = cancel;
        FormClosing += (_, e) => { if (installing) e.Cancel = true; };
    }

    private async Task InstallClickedAsync()
    {
        if (installedDirectory != null)
        {
            if (launch.Checked)
            {
                try { Process.Start(new ProcessStartInfo(Path.Combine(installedDirectory, "current", "SpaceLens.exe")) { UseShellExecute = true }); }
                catch (Exception ex) { status.Text = "启动失败：" + ex.Message; return; }
            }
            Close();
            return;
        }
        try
        {
            string destination = Installation.ValidateDirectory(Destination.Text);
            Destination.Text = destination;
            installing = true;
            Destination.Enabled = Browse.Enabled = Install.Enabled = cancel.Enabled = false;
            progress.Visible = true;
            progress.Style = ProgressBarStyle.Marquee;
            status.Text = "正在安装，请稍候…";
            await Installation.InstallAsync(destination);
            installedDirectory = destination;
            status.Text = "安装完成。";
            Install.Text = "完成";
            cancel.Visible = false;
        }
        catch (UnauthorizedAccessException)
        {
            status.Text = "无法写入此目录，请选择当前账户可以写入的文件夹。";
        }
        catch (Exception ex) { status.Text = ex.Message; }
        finally
        {
            installing = false;
            progress.Visible = false;
            Install.Enabled = cancel.Enabled = true;
            Destination.Enabled = Browse.Enabled = installedDirectory == null;
        }
    }
}
