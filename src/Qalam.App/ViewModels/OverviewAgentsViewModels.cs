using Qalam.Core.Agents;
using Qalam.Core.Context;
using Qalam.Core.Models;
using Qalam.Core.Workflow;

namespace Qalam.App.ViewModels;

public sealed class StatTile
{
    public string Label { get; init; } = "";
    public string Value { get; init; } = "";
    public string Color { get; init; } = "#111827";
}

public sealed class ContextHealthRow
{
    public string Name { get; init; } = "";
    public string State { get; init; } = "";
    public string Color { get; init; } = "";
}

public sealed class RecentActivityRow
{
    public string At { get; init; } = "";
    public string Project { get; init; } = "";
    public string Text { get; init; } = "";
    public string Color { get; init; } = "";
}

public sealed class OverviewViewModel : SectionViewModel
{
    private readonly MainViewModel _main;
    public override string Title => "Overview";
    public override string Icon => "";

    public ObservableCollection<StatTile> Tiles { get; } = new();
    public ObservableCollection<ContextHealthRow> ContextHealth { get; } = new();
    public ObservableCollection<RecentActivityRow> Recent { get; } = new();
    public ObservableCollection<ProjectRow> NeedsAttention { get; } = new();
    public string SetupHint { get; private set; } = "";
    public bool ShowSetupHint => SetupHint.Length > 0;

    public RelayCommand NewProjectCommand { get; }
    public RelayCommand OpenSettingsCommand { get; }
    public RelayCommand OpenBrandCommand { get; }
    public RelayCommand OpenProjectCommand { get; }

    public OverviewViewModel(MainViewModel main)
    {
        _main = main;
        NewProjectCommand = new RelayCommand(() => { _main.Navigate(_main.Projects); _main.Projects.NewCommand.Execute(null); });
        OpenSettingsCommand = new RelayCommand(() => _main.Navigate(_main.Settings));
        OpenBrandCommand = new RelayCommand(() => _main.Navigate(_main.Brand));
        OpenProjectCommand = new RelayCommand(p =>
        {
            if (p is ProjectRow row) { _main.Navigate(_main.Projects); _main.Projects.Select(row.Id); }
        });
    }

    public override void Refresh()
    {
        var s = _main.Services;
        var projects = s.Repository.ListProjects();

        Tiles.Clear();
        Tiles.Add(new StatTile { Label = "Projects", Value = projects.Count.ToString() });
        Tiles.Add(new StatTile { Label = "Running", Value = projects.Count(p => p.Status == ProjectStatus.Running).ToString(), Color = "#2563EB" });
        Tiles.Add(new StatTile { Label = "Need your input", Value = projects.Count(p => p.Status is ProjectStatus.NeedsInput or ProjectStatus.Failed).ToString(), Color = "#D97706" });
        Tiles.Add(new StatTile { Label = "Awaiting approval", Value = projects.Count(p => p.Status == ProjectStatus.AwaitingApproval).ToString(), Color = "#7C3AED" });
        Tiles.Add(new StatTile { Label = "Approved", Value = projects.Count(p => p.Status == ProjectStatus.Approved).ToString(), Color = "#059669" });

        ContextHealth.Clear();
        int incomplete = 0;
        foreach (var kind in ContextFiles.All)
        {
            var count = ContextValidator.CountPlaceholders(s.Context.Read(kind));
            if (count > 0) incomplete++;
            ContextHealth.Add(new ContextHealthRow
            {
                Name = ContextFiles.DisplayName(kind),
                State = count == 0 ? "Complete" : $"{count} item(s) to fill in",
                Color = count == 0 ? "#059669" : "#D97706",
            });
        }

        var hints = new List<string>();
        if (!s.Secrets.HasApiKey) hints.Add("Add your Claude API key in Settings.");
        if (incomplete > 0) hints.Add($"Fill in {incomplete} context file(s) under Brand Guidelines so agents don't have to stop and ask.");
        SetupHint = hints.Count == 0 ? "" : "Getting started: " + string.Join(" ", hints);

        NeedsAttention.Clear();
        foreach (var p in projects.Where(p => p.Status is ProjectStatus.NeedsInput or ProjectStatus.Failed or ProjectStatus.AwaitingApproval).Take(8))
            NeedsAttention.Add(new ProjectRow
            {
                Id = p.Id, Title = p.Title, Status = MainViewModel.StatusText(p.Status),
                StatusColor = MainViewModel.StatusColor(p.Status), Updated = UiHelpers.Local(p.UpdatedAt),
            });

        Recent.Clear();
        foreach (var (p, a) in projects.SelectMany(pr => pr.Activity.Select(ac => (Project: pr, Entry: ac))).OrderByDescending(x => x.Entry.At).Take(20))
            Recent.Add(new RecentActivityRow
            {
                At = UiHelpers.Local(a.At), Project = p.Title, Text = a.Text,
                Color = a.Level switch { ActivityLevel.Error => "#DC2626", ActivityLevel.Warning => "#D97706", _ => "#374151" },
            });

        OnPropertyChanged(nameof(SetupHint));
        OnPropertyChanged(nameof(ShowSetupHint));
    }
}

public sealed class AgentCard
{
    public string Name { get; init; } = "";
    public string Role { get; init; } = "";
    public string Responsibilities { get; init; } = "";
    public string ContextFiles { get; init; } = "";
    public string Accent { get; init; } = "";
    public string State { get; init; } = "";
    public string StateColor { get; init; } = "";
    public string CurrentTask { get; init; } = "";
    public string Completed { get; init; } = "";
    public List<string> RecentWork { get; init; } = new();
    public string? LastError { get; init; }
    public bool UsesWeb { get; init; }
}

public sealed class AgentsViewModel : SectionViewModel
{
    private readonly MainViewModel _main;
    public override string Title => "AI Agents";
    public override string Icon => "";

    public ObservableCollection<AgentCard> Agents { get; } = new();

    public AgentsViewModel(MainViewModel main) => _main = main;

    public override void Refresh()
    {
        var s = _main.Services;
        var versions = s.Repository.ListAllVersions();
        var projects = s.Repository.ListProjects().ToDictionary(p => p.Id, p => p.Title);
        Agents.Clear();
        foreach (var a in AgentCatalog.All)
        {
            var live = s.Tracker.Get(a.Id);
            var mine = versions.Where(v => v.Agent == a.Id && !v.IsIncomplete).ToList();
            Agents.Add(new AgentCard
            {
                Name = a.Name,
                Role = a.Role,
                Responsibilities = string.Join("\n", a.Responsibilities.Select(r => "• " + r)),
                ContextFiles = "Reads: " + string.Join(", ", a.ContextFiles.Select(Core.Context.ContextFiles.DisplayName)) +
                               (a.ReadsBrandDocuments ? ", uploaded brand documents" : ""),
                Accent = a.Accent,
                UsesWeb = a.UsesWebSearch && s.Settings.EnableWebSearch,
                State = live.State switch
                {
                    AgentWorkState.Working => "Working",
                    AgentWorkState.Failed => "Last task failed",
                    _ => "Idle"
                },
                StateColor = live.State switch
                {
                    AgentWorkState.Working => "#2563EB",
                    AgentWorkState.Failed => "#DC2626",
                    _ => "#6B7280"
                },
                CurrentTask = live.State == AgentWorkState.Working ? live.CurrentTask ?? "" : "No assigned task right now",
                Completed = $"{mine.Count} completed output(s)",
                RecentWork = mine.Take(3).Select(v =>
                    $"{v.Kind} v{v.VersionNumber} — {(projects.TryGetValue(v.ProjectId, out var t) ? t : "Tools")} ({UiHelpers.Local(v.CreatedAt)})").ToList(),
                LastError = live.State == AgentWorkState.Failed ? live.LastError : null,
            });
        }
    }
}
