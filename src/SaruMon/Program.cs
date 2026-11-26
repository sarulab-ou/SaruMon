namespace SaruMon;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--traffic-helper")
        {
            Environment.ExitCode = Monitoring.TrafficMonitorHelper.RunAsync(args).GetAwaiter().GetResult();
            return;
        }
        ApplicationConfiguration.Initialize();
        using var instance = new SingleInstance();
        if (!instance.IsPrimary)
        {
            if (!instance.ActivateExistingAsync().GetAwaiter().GetResult())
                MessageBox.Show("起動中の SaruMon に接続できませんでした。少し待ってからもう一度お試しください。", "SaruMon", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        InstallationInfo.RefreshVersion();
        using var window = new MainForm(args.Contains("--tray", StringComparer.OrdinalIgnoreCase));
        _ = window.Handle;
        instance.StartListening(() =>
        {
            try
            {
                if (!window.IsDisposed && !window.Disposing && window.IsHandleCreated)
                    window.BeginInvoke(() => { if (!window.IsDisposed && !window.Disposing) window.ShowFromTray(); });
            }
            catch (InvalidOperationException) { /* The main window is shutting down. */ }
        });
        Application.Run(window);
    }
}
