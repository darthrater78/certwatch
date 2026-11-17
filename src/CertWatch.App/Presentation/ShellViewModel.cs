using System.Collections.ObjectModel;
using System.Reflection;

namespace CertWatch.App.Presentation;

internal sealed class ShellViewModel : Observable
{
    public const string Repository = "https://github.com/darthrater78/certwatch";

    public ShellViewModel()
    {
        var informational = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0";
        Version = informational.Split('+')[0];
    }

    /// <summary>The app's date version (e.g. 2026.11.16), from the build.</summary>
    public string Version { get; }
    public string VersionText => $"v{Version}";
    public Uri RepoUrl => new(Repository);
    public Uri ReleaseNotesUrl => new($"{Repository}/releases/tag/v{Version}");

    public ObservableCollection<BrowserTabViewModel> Tabs { get; } = [];

    private BrowserTabViewModel? _selectedTab;
    public BrowserTabViewModel? SelectedTab { get => _selectedTab; set => Set(ref _selectedTab, value); }

    private bool _hasTabs;
    public bool HasTabs { get => _hasTabs; set => Set(ref _hasTabs, value); }

    private string _themeText = "Theme: System";
    public string ThemeText { get => _themeText; set => Set(ref _themeText, value); }
}
