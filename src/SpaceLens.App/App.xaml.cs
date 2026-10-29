using System.IO;
using System.Windows;

namespace SpaceLens.App;

public partial class App : Application
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Prefer predictable first-frame composition over driver-specific D3D surface behavior.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        Velopack.VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    internal SpaceLens.Updates.UpdateService? Updates { get; private set; }
    private readonly System.Windows.Threading.DispatcherTimer updateTimer = new() { Interval = TimeSpan.FromHours(4) };
    private void OnStartup(object sender, StartupEventArgs e)
    {
        if (e.Args.Contains(SpaceLens.Tasks.MemoryOptimizer.HelperArgument))
        {
            // Helper startup must never create the normal task/disk window or accept extra arguments.
            Shutdown(e.Args.Length == 1 ? SpaceLens.Tasks.MemoryOptimizer.RunHelper() : 87);
            return;
        }
        ThemeManager.Initialize();
        DispatcherUnhandledException += (_, args) =>
        {
            // Only recover expected shell / clipboard / I/O failures; don't silently hide programming faults.
            if (args.Exception is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
            {
                if (MainWindow is MainWindow window) window.Notify("操作未完成：" + args.Exception.Message);
                args.Handled = true;
            }
        };
        bool renderVerify = e.Args.Length > 1 && e.Args[0] == "--verify-render";
        bool menuVerify = e.Args.Length > 1 && e.Args[0] == "--verify-menus";
        bool interactionVerify = e.Args.Length > 1 && e.Args[0] == "--verify-interactions";
        bool inputVerify = e.Args.Length > 1 && e.Args[0] == "--verify-input";
        bool verify = e.Args.Length > 1 && e.Args[0] == "--verify-ui";
        bool taskVerify = e.Args.Length > 1 && e.Args[0] == "--verify-tasks";
        bool startupVerify = e.Args.Length > 1 && e.Args[0] == "--verify-startup";
        bool deletionVerify = e.Args.Length > 1 && e.Args[0] == "--verify-deletion";
        bool verification = verify || renderVerify || menuVerify || interactionVerify || inputVerify || taskVerify || startupVerify || deletionVerify;
        if (!verification)
        {
            Updates = new(new SpaceLens.Updates.ReleaseUpdater(), new(SpaceLens.Updates.UpdateSettings.DefaultPath));
            updateTimer.Tick += async (_, _) => await Updates.CheckAsync();
            Exit += (_, _) => { updateTimer.Stop(); Updates.PrepareForExit(); };
        }
        var main = new MainWindow(verification ? null : e.Args.FirstOrDefault(x => !x.StartsWith("--")));
        MainWindow = main;
        if (verify) main.Loaded += async (_, _) => await main.VerifyUiAsync(e.Args[1]);
        if (renderVerify) main.Loaded += async (_, _) => await main.VerifyRenderAsync(e.Args[1]);
        if (menuVerify) main.Loaded += async (_, _) => await main.VerifyMenusAsync(e.Args[1]);
        if (interactionVerify) main.Loaded += async (_, _) => await main.VerifyInteractionsAsync(e.Args[1]);
        if (inputVerify) main.Loaded += async (_, _) => await main.VerifyInputAsync(e.Args[1]);
        if (taskVerify) main.Loaded += async (_, _) => await main.VerifyTasksAsync(e.Args[1]);
        else if (startupVerify || !verification && (e.Args.Contains("--tasks") || !e.Args.Any(x => !x.StartsWith("--")))) main.SetTaskMode(true);
        if (startupVerify)
        {
            if (!main.VerifyBeforeFirstFrame(e.Args[1])) { Shutdown(1); return; }
            main.Loaded += async (_, _) => await main.VerifyStartupAsync(e.Args[1]);
        }
        if (deletionVerify) main.Loaded += async (_, _) => await main.VerifyDeletionAsync(e.Args[1]);
        main.Show();
        if (Updates is not null) _ = StartBackgroundUpdatesAsync();
    }

    private async Task StartBackgroundUpdatesAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(15));
        if (MainWindow?.IsVisible != true || Updates is null) return;
        await Updates.CheckAsync();
        if (MainWindow?.IsVisible == true) updateTimer.Start();
    }
}
