using Qalam.Core.Agents;
using Qalam.Core.Models;
using Qalam.Core.Workflow;

namespace Qalam.App.ViewModels;

public sealed record Option<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

public sealed class ProjectRow
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Format { get; init; } = "";
    public string Status { get; init; } = "";
    public string StatusColor { get; init; } = "";
    public string Updated { get; init; } = "";
    public bool QaApproved { get; init; }
}

public sealed class NewProjectForm : ObservableObject
{
    private string _topic = "", _title = "", _wordCount = "", _audience = "", _tone = "", _language = "",
        _platform = "", _keyword = "", _notes = "";
    private Option<ContentFormat> _format;
    private bool _startNow = true;

    public static IReadOnlyList<Option<ContentFormat>> Formats { get; } =
        Enum.GetValues<ContentFormat>().Select(f => new Option<ContentFormat>(f, StagePrompts.FormatName(f))).ToList();

    public NewProjectForm() => _format = Formats[0];

    public string Topic { get => _topic; set => Set(ref _topic, value); }
    public string Title { get => _title; set => Set(ref _title, value); }
    public Option<ContentFormat> Format { get => _format; set => Set(ref _format, value); }
    public string WordCount { get => _wordCount; set => Set(ref _wordCount, value); }
    public string Audience { get => _audience; set => Set(ref _audience, value); }
    public string Tone { get => _tone; set => Set(ref _tone, value); }
    public string Language { get => _language; set => Set(ref _language, value); }
    public string Platform { get => _platform; set => Set(ref _platform, value); }
    public string Keyword { get => _keyword; set => Set(ref _keyword, value); }
    public string Notes { get => _notes; set => Set(ref _notes, value); }
    public bool StartNow { get => _startNow; set => Set(ref _startNow, value); }

    public ContentBrief ToBrief() => new()
    {
        Topic = Topic.Trim(),
        Format = Format.Value,
        WordCount = int.TryParse(WordCount, out var w) && w > 0 ? w : null,
        Audience = Blank(Audience), Tone = Blank(Tone), Language = Blank(Language),
        Platform = Blank(Platform), PrimaryKeyword = Blank(Keyword), Notes = Blank(Notes),
    };

    private static string? Blank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

public sealed class ProjectsViewModel : SectionViewModel
{
    private readonly MainViewModel _main;
    private ProjectRow? _selected;
    private ProjectDetailViewModel? _detail;
    private bool _isCreating;
    private NewProjectForm _form = new();

    public override string Title => "Content Projects";
    public override string Icon => "";

    public ObservableCollection<ProjectRow> Projects { get; } = new();

    public ProjectRow? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            if (value != null) IsCreating = false;
            LoadDetail();
        }
    }

    public ProjectDetailViewModel? Detail
    {
        get => _detail;
        private set { if (Set(ref _detail, value)) OnPropertyChanged(nameof(ShowEmpty)); }
    }

    public bool IsCreating
    {
        get => _isCreating;
        set { if (Set(ref _isCreating, value)) OnPropertyChanged(nameof(ShowEmpty)); }
    }

    public bool ShowEmpty => _detail == null && !_isCreating;
    public NewProjectForm Form { get => _form; private set => Set(ref _form, value); }

    public RelayCommand NewCommand { get; }
    public RelayCommand CancelNewCommand { get; }
    public RelayCommand CreateCommand { get; }

    public ProjectsViewModel(MainViewModel main)
    {
        _main = main;
        NewCommand = new RelayCommand(() => { Form = new NewProjectForm(); IsCreating = true; Selected = null; });
        CancelNewCommand = new RelayCommand(() => IsCreating = false);
        CreateCommand = new RelayCommand(Create, () => !string.IsNullOrWhiteSpace(Form.Topic));
    }

    public void StartNewFromCalendar(CalendarEntry entry)
    {
        var form = new NewProjectForm
        {
            Topic = entry.Title,
            Keyword = entry.Keyword,
            Platform = entry.Platform,
            Notes = string.Join(" ", new[] { entry.Category, entry.Notes }.Where(s => !string.IsNullOrWhiteSpace(s))),
        };
        var match = NewProjectForm.Formats.FirstOrDefault(f =>
            f.Label.Equals(entry.Format, StringComparison.OrdinalIgnoreCase) ||
            entry.Format.Contains(f.Label, StringComparison.OrdinalIgnoreCase));
        if (match != null) form.Format = match;
        Form = form;
        IsCreating = true;
        Selected = null;
        _main.Navigate(this);
    }

    private void Create()
    {
        var project = _main.Services.Workflow.CreateProject(Form.ToBrief(), Form.Title);
        var start = Form.StartNow;
        IsCreating = false;
        Refresh();
        Select(project.Id);
        if (start) _main.StartProject(project.Id);
    }

    public void Select(string projectId)
    {
        Selected = Projects.FirstOrDefault(p => p.Id == projectId);
    }

    public override void Refresh()
    {
        var keep = _selected?.Id;
        var all = _main.Services.Repository.ListProjects();
        Projects.Clear();
        foreach (var p in all)
        {
            Projects.Add(new ProjectRow
            {
                Id = p.Id,
                Title = p.Title,
                Format = StagePrompts.FormatName(p.Brief.Format),
                Status = MainViewModel.StatusText(p.Status) + (_main.Services.Workflow.IsRunning(p.Id) && p.Status != ProjectStatus.Running ? " (finishing)" : ""),
                StatusColor = MainViewModel.StatusColor(p.Status),
                Updated = UiHelpers.Local(p.UpdatedAt),
                QaApproved = p.QaApproved,
            });
        }
        _selected = keep == null ? null : Projects.FirstOrDefault(p => p.Id == keep);
        OnPropertyChanged(nameof(Selected));
        LoadDetail();
    }

    private void LoadDetail()
    {
        if (_selected == null) { Detail = null; return; }
        var project = _main.Services.Repository.GetProject(_selected.Id);
        if (project == null) { Detail = null; return; }
        if (Detail == null || Detail.Id != project.Id) Detail = new ProjectDetailViewModel(_main, project.Id);
        Detail.Load(project);
    }
}

public sealed class StageRow
{
    public string Number { get; init; } = "";
    public string Title { get; init; } = "";
    public string Agent { get; init; } = "";
    public string Status { get; init; } = "";
    public string StatusColor { get; init; } = "";
    public string Time { get; init; } = "";
    public string? Error { get; init; }
}

public sealed class MessageRow
{
    public string At { get; init; } = "";
    public string Route { get; init; } = "";
    public string Type { get; init; } = "";
    public string Summary { get; init; } = "";
}

public sealed class ActivityRow
{
    public string At { get; init; } = "";
    public string Level { get; init; } = "";
    public string Color { get; init; } = "";
    public string Text { get; init; } = "";
}

public sealed class VersionRow
{
    public ArtifactVersion Version { get; init; } = null!;
    public string Label { get; init; } = "";
    public string Meta { get; init; } = "";
}

public sealed class ProjectDetailViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private Project? _project;
    private string _answer = "";
    private VersionRow? _selectedVersion;

    public string Id { get; }
    public string Title => _project?.Title ?? "";
    public string Status => _project == null ? "" : MainViewModel.StatusText(_project.Status);
    public string StatusColor => _project == null ? "#6B7280" : MainViewModel.StatusColor(_project.Status);
    public string? StatusDetail => _project?.StatusDetail;
    public string BriefSummary { get; private set; } = "";
    public bool IsRunning => _main.Services.Workflow.IsRunning(Id);
    public bool HasQuestions => Questions.Count > 0;
    public bool CanReview => _project?.Status is ProjectStatus.AwaitingApproval or ProjectStatus.Approved;
    public string RevisionInfo => _project == null ? "" :
        $"Revision rounds: {_project.RevisionRound}   ·   QA: {(_project.QaApproved ? "approved" : "not approved yet")}";

    public ObservableCollection<StageRow> Stages { get; } = new();
    public ObservableCollection<string> Questions { get; } = new();
    public ObservableCollection<MessageRow> Messages { get; } = new();
    public ObservableCollection<ActivityRow> Activity { get; } = new();
    public ObservableCollection<VersionRow> Versions { get; } = new();

    public string Answer { get => _answer; set => Set(ref _answer, value); }

    public VersionRow? SelectedVersion
    {
        get => _selectedVersion;
        set
        {
            if (!Set(ref _selectedVersion, value)) return;
            OnPropertyChanged(nameof(VersionContent));
            OnPropertyChanged(nameof(VersionSources));
        }
    }

    public string VersionContent => _selectedVersion?.Version.Content ?? "Select a version to read it.";

    public IReadOnlyList<SourceLink> VersionSources => _selectedVersion == null
        ? Array.Empty<SourceLink>()
        : _selectedVersion.Version.Sources.Concat(_selectedVersion.Version.ConsultedSources)
            .GroupBy(s => s.Url).Select(g => g.First()).ToList();

    public RelayCommand StartCommand { get; }
    public RelayCommand PauseCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand SendAnswerCommand { get; }
    public RelayCommand SkipAnswerCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand ReviewCommand { get; }
    public RelayCommand WorkspaceCommand { get; }
    public RelayCommand OpenBrandCommand { get; }
    public RelayCommand OpenSourceCommand { get; }
    public RelayCommand CopyVersionCommand { get; }

    public ProjectDetailViewModel(MainViewModel main, string id)
    {
        _main = main;
        Id = id;
        var wf = main.Services.Workflow;
        StartCommand = new RelayCommand(() => _main.StartProject(Id),
            () => _project != null && !IsRunning && _project.Status is ProjectStatus.NotStarted or ProjectStatus.Paused or ProjectStatus.Failed);
        PauseCommand = new RelayCommand(() => wf.RequestPause(Id), () => IsRunning);
        StopCommand = new RelayCommand(() => wf.Stop(Id), () => IsRunning);
        SendAnswerCommand = new RelayCommand(() =>
        {
            wf.AnswerQuestions(Id, Answer);
            Answer = "";
            _main.StartProject(Id);
        }, () => HasQuestions && !string.IsNullOrWhiteSpace(Answer));
        SkipAnswerCommand = new RelayCommand(() =>
        {
            if (!UiHelpers.Confirm("Continue without answering? The agent will proceed and mark anything unknown instead of guessing.")) return;
            wf.AnswerQuestions(Id, "");
            _main.StartProject(Id);
        }, () => HasQuestions);
        DeleteCommand = new RelayCommand(() =>
        {
            if (!UiHelpers.Confirm($"Delete \"{Title}\" and all its versions? This cannot be undone.")) return;
            wf.DeleteProject(Id);
            _main.Projects.Refresh();
        }, () => !IsRunning);
        ReviewCommand = new RelayCommand(() => { _main.Review.SelectProject(Id); _main.Navigate(_main.Review); });
        WorkspaceCommand = new RelayCommand(() => { _main.Workspace.SelectProject(Id); _main.Navigate(_main.Workspace); },
            () => _project?.CurrentDraftVersionId != null);
        OpenBrandCommand = new RelayCommand(() => _main.Navigate(_main.Brand));
        OpenSourceCommand = new RelayCommand(p => UiHelpers.OpenUrl((p as SourceLink)?.Url));
        CopyVersionCommand = new RelayCommand(() => UiHelpers.CopyToClipboard(_selectedVersion?.Version.Content), () => _selectedVersion != null);
    }

    public void Load(Project p)
    {
        _project = p;
        var b = p.Brief;
        BriefSummary = $"{StagePrompts.FormatName(b.Format)}" +
                       (b.WordCount is > 0 ? $" · {b.WordCount} words" : "") +
                       (string.IsNullOrWhiteSpace(b.Language) ? "" : $" · {b.Language}") +
                       (string.IsNullOrWhiteSpace(b.Platform) ? "" : $" · {b.Platform}") +
                       $" · created {UiHelpers.Local(p.CreatedAt)}";

        Stages.Clear();
        int n = 1;
        foreach (var s in p.Stages)
        {
            Stages.Add(new StageRow
            {
                Number = (n++).ToString(),
                Title = StagePrompts.StageTitle(s.Kind),
                Agent = s.Agent is { } a ? AgentCatalog.Get(a).Name : "You (final approval)",
                Status = s.Status switch
                {
                    StageStatus.NeedsInput => "Needs input",
                    StageStatus.Incomplete => "Incomplete",
                    _ => s.Status.ToString()
                },
                StatusColor = s.Status switch
                {
                    StageStatus.Completed => "#059669",
                    StageStatus.Running => "#2563EB",
                    StageStatus.Failed or StageStatus.Incomplete => "#DC2626",
                    StageStatus.NeedsInput => "#D97706",
                    _ => "#9CA3AF"
                },
                Time = s.CompletedAt is { } c && s.Status == StageStatus.Completed ? UiHelpers.Local(c)
                    : s.StartedAt is { } st && s.Status == StageStatus.Running ? "since " + UiHelpers.Local(st) : "",
                Error = s.Error,
            });
        }

        Questions.Clear();
        foreach (var q in p.Stages.Where(s => s.Status == StageStatus.NeedsInput).SelectMany(s => s.Questions)) Questions.Add(q);

        Messages.Clear();
        foreach (var m in p.Messages.AsEnumerable().Reverse())
            Messages.Add(new MessageRow { At = UiHelpers.Local(m.At), Route = $"{m.From} → {m.To}", Type = m.Type.ToString(), Summary = m.Summary });

        Activity.Clear();
        foreach (var a in p.Activity.AsEnumerable().Reverse())
            Activity.Add(new ActivityRow
            {
                At = UiHelpers.Local(a.At),
                Level = a.Level.ToString(),
                Color = a.Level switch { ActivityLevel.Error => "#DC2626", ActivityLevel.Warning => "#D97706", _ => "#6B7280" },
                Text = a.Text,
            });

        var keep = _selectedVersion?.Version.Id;
        Versions.Clear();
        foreach (var v in _main.Services.Repository.ListVersions(p.Id).Reverse())
        {
            Versions.Add(new VersionRow
            {
                Version = v,
                Label = $"{v.Kind} v{v.VersionNumber}" + (v.Id == p.CurrentDraftVersionId ? "  (current draft)" : ""),
                Meta = $"{v.Author} · {UiHelpers.Local(v.CreatedAt)}" + (v.Note != null ? " · " + v.Note : "") +
                       (v.IsIncomplete ? " · INCOMPLETE" : ""),
            });
        }
        _selectedVersion = keep == null ? Versions.FirstOrDefault() : Versions.FirstOrDefault(v => v.Version.Id == keep) ?? Versions.FirstOrDefault();

        OnPropertyChanged(string.Empty); // refresh all bindings
    }
}
