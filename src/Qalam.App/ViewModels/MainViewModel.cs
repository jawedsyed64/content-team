using System.Windows.Threading;
using Qalam.App.Services;
using Qalam.Core.Models;

namespace Qalam.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly DispatcherTimer _debounce;
    private SectionViewModel _current;

    public AppServices Services { get; }
    public ObservableCollection<SectionViewModel> Sections { get; }

    public OverviewViewModel Overview { get; }
    public AgentsViewModel Agents { get; }
    public ProjectsViewModel Projects { get; }
    public ResearchLibraryViewModel Research { get; }
    public CalendarViewModel Calendar { get; }
    public WritingWorkspaceViewModel Workspace { get; }
    public SeoToolsViewModel Seo { get; }
    public SocialMediaViewModel Social { get; }
    public EditorialReviewViewModel Review { get; }
    public BrandGuidelinesViewModel Brand { get; }
    public SettingsViewModel Settings { get; }

    public SectionViewModel Current
    {
        get => _current;
        set
        {
            if (Set(ref _current, value)) value.OnActivated();
        }
    }

    public string StatusBarText =>
        (Services.Secrets.HasApiKey ? "Claude API key: set" : "Claude API key: NOT SET — open Settings") +
        $"   ·   Model: {Services.Settings.Model}   ·   Web search: {(Services.Settings.EnableWebSearch ? "on" : "off")}" +
        $"   ·   Data: {Services.DataFolder}";

    public MainViewModel(AppServices services)
    {
        Services = services;
        Overview = new OverviewViewModel(this);
        Agents = new AgentsViewModel(this);
        Projects = new ProjectsViewModel(this);
        Research = new ResearchLibraryViewModel(this);
        Calendar = new CalendarViewModel(this);
        Workspace = new WritingWorkspaceViewModel(this);
        Seo = new SeoToolsViewModel(this);
        Social = new SocialMediaViewModel(this);
        Review = new EditorialReviewViewModel(this);
        Brand = new BrandGuidelinesViewModel(this);
        Settings = new SettingsViewModel(this);

        Sections = new ObservableCollection<SectionViewModel>
        {
            Overview, Agents, Projects, Research, Calendar, Workspace, Seo, Social, Review, Brand, Settings
        };
        _current = Overview;

        // Coalesce bursts of background events into one UI refresh.
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Current.Refresh();
            OnPropertyChanged(nameof(StatusBarText));
        };
        services.Workflow.ProjectChanged += (_, _) => UiHelpers.OnUi(Kick);
        services.Tracker.Changed += (_, _) => UiHelpers.OnUi(Kick);

        Overview.Refresh();
    }

    private void Kick()
    {
        _debounce.Stop();
        _debounce.Start();
    }

    public void Navigate(SectionViewModel section) => Current = section;

    public void SettingsChanged() => OnPropertyChanged(nameof(StatusBarText));

    /// <summary>Runs a project in the background; errors are shown, never swallowed.</summary>
    public void StartProject(string projectId)
    {
        Task.Run(async () =>
        {
            try { await Services.Workflow.RunAsync(projectId); }
            catch (Exception ex) { UiHelpers.OnUi(() => UiHelpers.ShowError(ex)); }
        });
    }

    public static string StatusText(ProjectStatus s) => s switch
    {
        ProjectStatus.NotStarted => "Not started",
        ProjectStatus.Running => "Running",
        ProjectStatus.Paused => "Paused",
        ProjectStatus.NeedsInput => "Needs your input",
        ProjectStatus.Failed => "Stopped — error",
        ProjectStatus.AwaitingApproval => "Awaiting your approval",
        ProjectStatus.Approved => "Approved",
        ProjectStatus.Rejected => "Rejected",
        _ => s.ToString()
    };

    public static string StatusColor(ProjectStatus s) => s switch
    {
        ProjectStatus.Running => "#2563EB",
        ProjectStatus.NeedsInput => "#D97706",
        ProjectStatus.Failed => "#DC2626",
        ProjectStatus.AwaitingApproval => "#7C3AED",
        ProjectStatus.Approved => "#059669",
        ProjectStatus.Rejected => "#6B7280",
        _ => "#6B7280"
    };
}
