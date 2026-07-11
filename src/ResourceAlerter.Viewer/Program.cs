namespace ResourceAlerter.Viewer;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        // Same default as the service's Database.Path; overridable with a command-line arg
        // (e.g. a shortcut pointing at a copied-over DB from another server).
        var databasePath = args.Length > 0
            ? args[0]
            : Environment.ExpandEnvironmentVariables(@"%ProgramData%\ResourceAlerter\resourcealerter.db");

        Application.Run(new MainForm(new DataReader(databasePath)));
    }
}
