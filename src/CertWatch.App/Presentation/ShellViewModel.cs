using System.Collections.ObjectModel;

namespace CertWatch.App.Presentation;

internal sealed class ShellViewModel : Observable
{
    public ObservableCollection<BrowserTabViewModel> Tabs { get; } = [];

    private BrowserTabViewModel? _selectedTab;
    public BrowserTabViewModel? SelectedTab
    {
        get => _selectedTab;
        set => Set(ref _selectedTab, value);
    }

    private bool _hasTabs;
    public bool HasTabs { get => _hasTabs; set => Set(ref _hasTabs, value); }
}
