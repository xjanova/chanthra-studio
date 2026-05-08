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

    public StudioContext()
    {
        Settings = AppSettings.Load();
        Db = new Database();
        Db.Bootstrap();
        Providers = new ProviderRegistry();
        Generation = new GenerationService(this);
    }
}
