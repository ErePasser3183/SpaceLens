using System.IO;
using System.Windows;

namespace SpaceLens.App;

public partial class App : Application
{
    [STAThread]
    private static void Main(string[] args)
    {
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
        bool verification = verify || renderVerify || menuVerify || interactionVerify || inputVerify || taskVerify;
        if (!verification)
        {
            Updates = new(new SpaceLens.Updates.ReleaseUpdater(), new(SpaceLens.Updates.UpdateSettings.DefaultPath));
            updateTimer.Tick += async (_, _) => await Updates.CheckAsync();
            Exit += (_, _) => { updateTimer.Stop(); Updates.PrepareForExit(); };
        }
        var main = new MainWindow(verify || renderVerify || menuVerify || interactionVerify || inputVerify || taskVerify ? null : e.Args.FirstOrDefault(x => !x.StartsWith("--")));
        MainWindow = main;
        if (verify) main.Loaded += async (_, _) => await main.VerifyUiAsync(e.Args[1]);
        if (renderVerify) main.Loaded += async (_, _) => await main.VerifyRenderAsync(e.Args[1]);
        if (menuVerify) main.Loaded += async (_, _) => await main.VerifyMenusAsync(e.Args[1]);
        if (interactionVerify) main.Loaded += async (_, _) => await main.VerifyInteractionsAsync(e.Args[1]);
        if (inputVerify) main.Loaded += async (_, _) => await main.VerifyInputAsync(e.Args[1]);
        if (taskVerify) main.Loaded += async (_, _) => await main.VerifyTasksAsync(e.Args[1]);
        else if (!(verify || renderVerify || menuVerify || interactionVerify || inputVerify) && (e.Args.Contains("--tasks") || !e.Args.Any(x => !x.StartsWith("--")))) main.Loaded += (_, _) => main.SetTaskMode(true);
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
