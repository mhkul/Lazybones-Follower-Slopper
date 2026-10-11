namespace AutoFollower.Gui;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if(typeof(Program).Assembly.GetManifestResourceNames().Contains("AutoFollower.Runtime.zip"))
            AutoFollower.BundledRuntime.Payload=()=>typeof(Program).Assembly.GetManifestResourceStream("AutoFollower.Runtime.zip")!;
        if (args.Length == 4 && args[0] == "--mo2-ini" && args[2] == "--mo2-executable")
        { ActiveMo2.LaunchIni = Path.GetFullPath(args[1]); ActiveMo2.LaunchExecutable = Path.GetFullPath(args[3]); }
        if (args.Length == 3 && args[0] == "--render-evidence")
        {
            MainForm.RenderEvidence(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
            return;
        }
        Application.Run(new MainForm());
    }
}
