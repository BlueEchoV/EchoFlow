namespace EchoFlow;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        using var mutex = new Mutex(true, @"Local\EchoFlow.SingleInstance", out bool firstInstance);
        if (!firstInstance) return;

        ApplicationConfiguration.Initialize();
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        // Starting with Windows passes --background: stay in the tray. Opening it yourself shows the window.
        Application.Run(new TrayApp(showWindow: !args.Contains("--background")));
    }
}
