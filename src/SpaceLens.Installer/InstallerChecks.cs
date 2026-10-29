using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace SpaceLens.Installer;

internal static class InstallerChecks
{
    public static int Run(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        string report = Path.Combine(outputDirectory, "installer-checks.txt");
        try
        {
            string fixture = Path.Combine(Path.GetFullPath(outputDirectory), Guid.NewGuid().ToString("N"));
            string chosen = Path.Combine(fixture, "中文 空格", "SpaceLens");
            Require(Installation.ValidateDirectory(chosen + "\\") == chosen, "中文、空格及末尾分隔符");
            Require(!Directory.Exists(fixture), "选择路径时不得创建文件夹");
            foreach (string invalid in new[] { "C:\\", "C:relative", "relative", "\\\\server\\share", "C:\\bad\"path" })
                Reject(invalid);
            Directory.CreateDirectory(fixture);
            File.WriteAllText(Path.Combine(fixture, "keep.txt"), "keep");
            Reject(fixture);
            Require(File.ReadAllText(Path.Combine(fixture, "keep.txt")) == "keep", "拒绝覆盖其他文件");
            string existing = Path.Combine(fixture, "existing");
            Directory.CreateDirectory(Path.Combine(existing, "current"));
            File.WriteAllText(Path.Combine(existing, "Update.exe"), "fixture");
            File.WriteAllText(Path.Combine(existing, "current", "sq.version"), "<package><metadata><id>OtherApp</id></metadata></package>");
            Reject(existing);
            File.WriteAllText(Path.Combine(existing, "current", "sq.version"), "<package><metadata><id>SpaceLens</id></metadata></package>");
            Require(Installation.ValidateDirectory(existing) == existing, "识别已有安装");
            File.WriteAllText(Path.Combine(existing, ".portable"), "");
            Reject(existing);
            var start = Installation.CreateStartInfo("payload.exe", chosen);
            Require(start.Arguments == "--silent --installto \"" + chosen + "\"" && !start.UseShellExecute, "目录参数独立引用且不经过命令解释器");
            using (var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("SpaceLens.Setup.exe"))
            {
                Require(payload != null && payload.Length > 1000000, "内嵌安装包存在");
                Require(payload!.ReadByte() == 'M' && payload.ReadByte() == 'Z', "安装包可执行文件头");
            }
            string? extracted = null;
            Installation.InstallAsync(chosen, info =>
            {
                extracted = info.FileName;
                Require(info.Arguments == start.Arguments, "实际传递所选目录");
                using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("SpaceLens.Setup.exe")!;
                using var file = File.OpenRead(info.FileName);
                using var sha = SHA256.Create();
                Require(Convert.ToBase64String(sha.ComputeHash(resource)) == Convert.ToBase64String(sha.ComputeHash(file)), "释放载荷与内嵌安装包一致");
                Directory.CreateDirectory(Path.Combine(chosen, "current"));
                File.WriteAllText(Path.Combine(chosen, "current", "SpaceLens.exe"), "test fixture");
                return Task.FromResult(0);
            }).GetAwaiter().GetResult();
            Require(extracted != null && !File.Exists(extracted), "完成后清理临时载荷");
            string failure = Path.Combine(fixture, "failed");
            bool rejected = false;
            try
            {
                Installation.InstallAsync(failure, info => { extracted = info.FileName; return Task.FromResult(23); }).GetAwaiter().GetResult();
            }
            catch (IOException ex) { rejected = ex.Message.Contains("23"); }
            Require(rejected && !File.Exists(extracted), "安装失败上报并清理载荷");
            Require(!Directory.EnumerateFileSystemEntries(failure).GetEnumerator().MoveNext(), "写入探针已清理，可重试");
            using (var window = new InstallWindow())
            {
                window.Show();
                System.Windows.Forms.Application.DoEvents();
                Require(window.Browse.Enabled && window.Install.Enabled && window.Destination.Enabled, "初始界面允许选择目录");
                Require(window.Destination.Text == Installation.DefaultDirectory, "默认路径");
                using var bitmap = new Bitmap(window.Width, window.Height);
                window.DrawToBitmap(bitmap, new Rectangle(0, 0, window.Width, window.Height));
                bitmap.Save(Path.Combine(outputDirectory, "installer.png"), ImageFormat.Png);
                window.Close();
            }
            File.WriteAllText(report, "PASS: 目录校验、中文空格、已有安装识别、其他目录保护、安装参数、载荷释放及校验、失败清理及重试、安装窗口。使用模拟安装后端，未修改本机安装和快捷方式。\n");
            return 0;
        }
        catch (Exception ex) { File.WriteAllText(report, ex.ToString()); return 1; }
    }

    private static void Reject(string path)
    {
        try { Installation.ValidateDirectory(path); }
        catch (IOException) { return; }
        throw new InvalidOperationException("应拒绝路径：" + path);
    }

    private static void Require(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
    }
}
