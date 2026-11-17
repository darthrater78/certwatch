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

    /// <summary>The app's date version (e.g. 2026.11.17), from the build.</summary>
    public string Version { get; }
    public string VersionText => $"v{Version}";
    public Uri RepoUrl => new(Repository);
    public Uri ReleaseNotesUrl => new($"{Repository}/releases/tag/v{Version}");

    public ObservableCollection<BrowserTabViewModel> Tabs { get; } = [];

    private BrowserTabViewModel? _selectedTab;
    public BrowserTabViewModel? SelectedTab { get => _selectedTab; set => Set(ref _selectedTab, value); }

    private bool _hasTabs;
    public bool HasTabs { get => _hasTabs; set => Set(ref _hasTabs, value); }

    private bool _anyChosen;
    /// <summary>False until at least one browser is ticked; the empty page then offers "Choose browsers…".</summary>
    public bool AnyChosen { get => _anyChosen; set => Set(ref _anyChosen, value); }

    private string _emptyText = "";
    public string EmptyText { get => _emptyText; set => Set(ref _emptyText, value); }

    private string _themeText = "Theme";
    public string ThemeText { get => _themeText; set => Set(ref _themeText, value); }

    private string _popupText = "Pop-ups: until dismissed";
    public string PopupText { get => _popupText; set => Set(ref _popupText, value); }
}

/// <summary>A browser the user can tick to monitor. Unticked browsers' address bars are never read.</summary>
internal sealed class BrowserChoice(string id, string name, bool monitored, bool running = false) : Observable
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    /// <summary>Whether the browser's process was running when the picker opened (process names only).</summary>
    public bool IsRunning { get; } = running;

    private bool _isMonitored = monitored;
    public bool IsMonitored { get => _isMonitored; set => Set(ref _isMonitored, value); }
}
