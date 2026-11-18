using System.Collections.ObjectModel;
using System.Reflection;
using CertWatch.Core.Docking;

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
    public bool HasTabs
    {
        get => _hasTabs;
        set { if (Set(ref _hasTabs, value)) OnLayoutChanged(); }
    }

    // ---- docking (DESIGN.md "Docked layouts") ----

    private DockPosition _dock;
    public DockPosition Dock
    {
        get => _dock;
        set { if (Set(ref _dock, value)) OnLayoutChanged(); }
    }
    public bool IsFloating => _dock == DockPosition.Floating;
    public bool IsSideDocked => _dock is DockPosition.Left or DockPosition.Right;
    public bool IsTopDocked => _dock == DockPosition.Top;
    /// <summary>The full window's tabs: floating with at least one browser.</summary>
    public bool ShowTabs => IsFloating && _hasTabs;
    public bool ShowSidePanel => IsSideDocked && _hasTabs;
    public bool ShowTopStrip => IsTopDocked && _hasTabs;
    /// <summary>The empty page (no browsers chosen), floating or docked at the side.</summary>
    public bool ShowEmptyPage => !_hasTabs && !IsTopDocked;
    /// <summary>The empty state squeezed into the top strip.</summary>
    public bool ShowTopEmpty => !_hasTabs && IsTopDocked;

    private string _dockHint = "";
    /// <summary>A note for the docked layouts, e.g. that the panel covers the browser's edge. Empty when none.</summary>
    public string DockHint { get => _dockHint; set => Set(ref _dockHint, value ?? ""); }

    private void OnLayoutChanged()
    {
        foreach (var name in new[] { nameof(IsFloating), nameof(IsSideDocked), nameof(IsTopDocked), nameof(ShowTabs), nameof(ShowSidePanel), nameof(ShowTopStrip), nameof(ShowEmptyPage), nameof(ShowTopEmpty) })
            OnChanged(name);
    }

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
