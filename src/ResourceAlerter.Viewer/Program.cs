using ResourceAlerter.Localization;

namespace ResourceAlerter.Viewer;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // A silent crash (no window, no message) is the worst failure mode for a small utility
        // like this — it just looks broken. Every failure path below ends in a MessageBox so
        // there's always visible feedback, even for exceptions during WinForms bootstrap itself.
        Application.ThreadException += (_, e) => ShowFatalError("unexpected error", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            ShowFatalError("unexpected error", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString() ?? "unknown error"));

        try
        {
            ApplicationConfiguration.Initialize();

            // Same file the "Configuración" button edits — read here too, before building any
            // UI, so the Viewer opens in whatever language was last saved (service and Viewer
            // always agree, since it's the same appsettings.json).
            var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            var config = ConfigStore.Load(configPath);
            Strings.CurrentLanguage = config.General.Language;

            // Must track the service's actual Database.Path (editable in Settings > Base de
            // datos), not a hardcoded copy of its default -- otherwise changing that field only
            // moves where the service writes, leaving the Viewer to keep looking at the old
            // location forever and report "database not found". A command-line arg still wins,
            // for a shortcut pointing at a copied-over DB from another server.
            var databasePath = args.Length > 0
                ? args[0]
                : config.Database.GetExpandedPath();

            Application.Run(new MainForm(new DataReader(databasePath)));
        }
        catch (Exception ex)
        {
            ShowFatalError("failed to start", ex);
        }
    }

    private static void ShowFatalError(string context, Exception ex)
    {
        MessageBox.Show(
            $"ResourceAlerter Viewer {context}:\r\n\r\n{ex}",
            "ResourceAlerter Viewer — Error",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }
}
