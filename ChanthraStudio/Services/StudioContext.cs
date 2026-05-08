using ChanthraStudio.Services.Providers;

namespace ChanthraStudio.Services;

/// <summary>
/// Lightweight composition root. Created once in <see cref="App"/>, exposed
/// to ViewModels via <c>App.Current.Studio</c>. Renamed off "AppContext" to
/// dodge the System.AppContext name clash in code-behind.
/// </summary>
public sealed class StudioContext
{
    public AppSettings Settings { get; }
    public Database Db { get; }
    public ProviderRegistry Providers { get; }
    public GenerationService Generation { get; }
    public ClipsRepository Clips { get; }
    public ChanthraStudio.Services.Providers.ComfyUI.WorkflowRepository Workflows { get; }

    public StudioContext()
    {
        // Order matters: the DB has to be bootstrapped before AppSettings.Load
        // can read its rows (and run the legacy-JSON import on first run).
        Db = new Database();
        Db.Bootstrap();
        Settings = AppSettings.Load(Db);
        Providers = new ProviderRegistry();
        Clips = new ClipsRepository(Db);
        Workflows = new ChanthraStudio.Services.Providers.ComfyUI.WorkflowRepository();
        Generation = new GenerationService(this);
    }
}
