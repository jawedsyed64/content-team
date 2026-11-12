using Microsoft.Win32;
using Qalam.Core.Models;
using Qalam.Core.Quality;
using Qalam.Core.Storage;
using Qalam.Core.Workflow;

namespace Qalam.App.ViewModels;

// ============================================================ Research Library

public sealed class ResearchLibraryViewModel : SectionViewModel
{
    private readonly MainViewModel _main;
    private string _search = "";
    private VersionRow? _selected;

    public override string Title => "Research Library";
    public override string Icon => "";

    public ObservableCollection<VersionRow> Briefs { get; } = new();
    public string Search { get => _search; set { if (Set(ref _search, value)) Refresh(); } }

    public VersionRow? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            OnPropertyChanged(nameof(Content));
            OnPropertyChanged(nameof(Sources));
        }
    }

    public string Content => _selected?.Version.Content ?? "Research briefs from every project appear here.";
    public IReadOnlyList<SourceLink> Sources => _selected == null ? Array.Empty<SourceLink>()
        : _selected.Version.Sources.Concat(_selected.Version.ConsultedSources).GroupBy(s => s.Url).Select(g => g.First()).ToList();

    public RelayCommand OpenSourceCommand { get; }
    public RelayCommand CopyCommand { get; }

    public ResearchLibraryViewModel(MainViewModel main)
    {
        _main = main;
        OpenSourceCommand = new RelayCommand(p => UiHelpers.OpenUrl((p as SourceLink)?.Url));
        CopyCommand = new RelayCommand(() => UiHelpers.CopyToClipboard(_selected?.Version.Content), () => _selected != null);
    }

    public override void Refresh()
    {
        var keep = _selected?.Version.Id;
        var titles = _main.Services.Repository.ListProjects().ToDictionary(p => p.Id, p => p.Title);
        Briefs.Clear();
        foreach (var v in _main.Services.Repository.ListAllVersions(ArtifactKind.ResearchBrief))
        {
            var title = titles.TryGetValue(v.ProjectId, out var t) ? t : "(deleted project)";
            if (_search.Length > 0 &&
                !title.Contains(_search, StringComparison.OrdinalIgnoreCase) &&
                !v.Content.Contains(_search, StringComparison.OrdinalIgnoreCase)) continue;
            Briefs.Add(new VersionRow
            {
                Version = v,
                Label = title,
                Meta = $"v{v.VersionNumber} · {UiHelpers.Local(v.CreatedAt)} · {v.Sources.Count} cited / {v.ConsultedSources.Count} consulted sources",
            });
        }
        _selected = Briefs.FirstOrDefault(b => b.Version.Id == keep) ?? Briefs.FirstOrDefault();
        OnPropertyChanged(nameof(Selected));
        OnPropertyChanged(nameof(Content));
        OnPropertyChanged(nameof(Sources));
    }
}

// ============================================================ Content Calendar

public sealed class CalendarViewModel : SectionViewModel
{
    private readonly MainViewModel _main;
    private DateTime _start = DateTime.Today;
    private bool _monthly = true;
    private string _focus = "";
    private string _questions = "";
    private CalendarEntry? _selected;

    public override string Title => "Content Calendar";
    public override string Icon => "";

    public ObservableCollection<CalendarEntry> Entries { get; } = new();
    public ObservableCollection<string> Themes { get; } = new();
    public DateTime Start { get => _start; set => Set(ref _start, value); }
    public bool Monthly { get => _monthly; set => Set(ref _monthly, value); }
    public string Focus { get => _focus; set => Set(ref _focus, value); }
    public string Questions { get => _questions; private set { Set(ref _questions, value); OnPropertyChanged(nameof(HasQuestions)); } }
    public bool HasQuestions => _questions.Length > 0;
    public CalendarEntry? Selected { get => _selected; set => Set(ref _selected, value); }

    public AsyncCommand GenerateCommand { get; }
    public RelayCommand CreateProjectCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand ClearCommand { get; }

    public CalendarViewModel(MainViewModel main)
    {
        _main = main;
        GenerateCommand = new AsyncCommand(GenerateAsync);
        CreateProjectCommand = new RelayCommand(() => _main.Projects.StartNewFromCalendar(_selected!), () => _selected != null);
        RemoveCommand = new RelayCommand(() =>
        {
            var data = _main.Services.Calendar.Load();
            data.Entries.RemoveAll(e => e.Id == _selected!.Id);
            _main.Services.Calendar.Save(data);
            Refresh();
        }, () => _selected != null);
        ClearCommand = new RelayCommand(() =>
        {
            if (!UiHelpers.Confirm("Remove all calendar entries?")) return;
            _main.Services.Calendar.Save(new Services.CalendarData());
            Refresh();
        });
    }

    private async Task GenerateAsync()
    {
        var start = Monthly ? new DateTime(Start.Year, Start.Month, Math.Max(1, Start.Day)) : Start;
        var result = await Task.Run(() => _main.Services.Workflow.GenerateCalendarAsync(start, Monthly, Focus));
        var data = _main.Services.Calendar.Load();
        data.Entries.AddRange(result.Entries);
        data.Entries = data.Entries.OrderBy(e => e.Date).ToList();
        if (result.Themes.Count > 0) data.Themes = result.Themes;
        _main.Services.Calendar.Save(data);
        Questions = result.Questions.Count == 0 ? "" :
            "The Content Strategist has questions (add the answers to your Content Strategy file and regenerate):\n• " +
            string.Join("\n• ", result.Questions);
        Refresh();
    }

    public override void Refresh()
    {
        var data = _main.Services.Calendar.Load();
        var keep = _selected?.Id;
        Entries.Clear();
        foreach (var e in data.Entries.OrderBy(e => e.Date)) Entries.Add(e);
        Themes.Clear();
        foreach (var t in data.Themes) Themes.Add(t);
        _selected = Entries.FirstOrDefault(e => e.Id == keep);
        OnPropertyChanged(nameof(Selected));
    }
}

// ============================================================ Writing Workspace

public sealed class WritingWorkspaceViewModel : SectionViewModel
{
    private readonly MainViewModel _main;
    private ProjectRow? _project;
    private VersionRow? _selectedVersion;
    private string _editorText = "";
    private bool _dirty;

    public override string Title => "Writing Workspace";
    public override string Icon => "";

    public ObservableCollection<ProjectRow> Projects { get; } = new();
    public ObservableCollection<VersionRow> Drafts { get; } = new();

    public ProjectRow? Project
    {
        get => _project;
        set
        {
            if (_dirty && value?.Id != _project?.Id && !UiHelpers.Confirm("Discard your unsaved edits?")) { OnPropertyChanged(); return; }
            if (Set(ref _project, value)) LoadDrafts(loadCurrent: true);
        }
    }

    public VersionRow? SelectedVersion
    {
        get => _selectedVersion;
        set { if (Set(ref _selectedVersion, value)) OnPropertyChanged(nameof(SelectedContent)); }
    }

    public string SelectedContent => _selectedVersion?.Version.Content ?? "";

    public string EditorText
    {
        get => _editorText;
        set
        {
            if (!Set(ref _editorText, value)) return;
            _dirty = true;
            OnPropertyChanged(nameof(WordCountText));
        }
    }

    public string WordCountText =>
        $"{System.Text.RegularExpressions.Regex.Matches(_editorText, @"\b[\p{L}\p{N}'’-]+\b").Count} words" + (_dirty ? " · unsaved changes" : "");

    public bool IsBusy => _project != null && _main.Services.Workflow.IsRunning(_project.Id);

    public RelayCommand SaveCommand { get; }
    public RelayCommand LoadVersionCommand { get; }
    public RelayCommand RecheckCommand { get; }
    public RelayCommand ExportCommand { get; }

    public WritingWorkspaceViewModel(MainViewModel main)
    {
        _main = main;
        SaveCommand = new RelayCommand(Save, () => _project != null && _dirty && !IsBusy);
        LoadVersionCommand = new RelayCommand(() => SetEditor(_selectedVersion!.Version.Content), () => _selectedVersion != null);
        RecheckCommand = new RelayCommand(() =>
        {
            _main.Services.Workflow.RequestQualityRecheck(_project!.Id);
            _main.StartProject(_project.Id);
            _main.Projects.Select(_project.Id);
        }, () => _project != null && !_dirty && !IsBusy && Drafts.Count > 0);
        ExportCommand = new RelayCommand(() => Exporter.Export(_project?.Title ?? "draft", _editorText), () => _editorText.Length > 0);
    }

    public void SelectProject(string id)
    {
        Refresh();
        Project = Projects.FirstOrDefault(p => p.Id == id);
    }

    private void Save()
    {
        _main.Services.Workflow.SaveUserEdit(_project!.Id, _editorText);
        _dirty = false;
        LoadDrafts(loadCurrent: false);
        OnPropertyChanged(nameof(WordCountText));
    }

    private void SetEditor(string text)
    {
        _editorText = text;
        _dirty = false;
        OnPropertyChanged(nameof(EditorText));
        OnPropertyChanged(nameof(WordCountText));
    }

    private void LoadDrafts(bool loadCurrent)
    {
        Drafts.Clear();
        if (_project == null) { SetEditor(""); return; }
        var p = _main.Services.Repository.GetProject(_project.Id);
        foreach (var v in _main.Services.Repository.ListVersions(_project.Id).Where(v => v.Kind == ArtifactKind.Draft).Reverse())
            Drafts.Add(new VersionRow
            {
                Version = v,
                Label = $"Draft v{v.VersionNumber}" + (v.Id == p?.CurrentDraftVersionId ? " (current)" : ""),
                Meta = $"{v.Author} · {UiHelpers.Local(v.CreatedAt)}" + (v.Note != null ? " · " + v.Note : ""),
            });
        SelectedVersion = Drafts.FirstOrDefault(d => d.Version.Id == p?.CurrentDraftVersionId) ?? Drafts.FirstOrDefault();
        if (loadCurrent) SetEditor(SelectedVersion?.Version.Content ?? "");
        OnPropertyChanged(nameof(IsBusy));
    }

    public override void Refresh()
    {
        var keep = _project?.Id;
        Projects.Clear();
        foreach (var p in _main.Services.Repository.ListProjects().Where(p => p.CurrentDraftVersionId != null))
            Projects.Add(new ProjectRow { Id = p.Id, Title = p.Title, Status = MainViewModel.StatusText(p.Status) });
        _project = Projects.FirstOrDefault(p => p.Id == keep);
        OnPropertyChanged(nameof(Project));
        if (_project != null && !_dirty) LoadDrafts(loadCurrent: false);
        OnPropertyChanged(nameof(IsBusy));
    }
}

// ============================================================ SEO Tools

public sealed class SeoToolsViewModel : SectionViewModel
{
    private readonly MainViewModel _main;
    private string _input = "", _keyword = "";
    private VersionRow? _selected;

    public override string Title => "SEO Tools";
    public override string Icon => "";

    public string Input { get => _input; set => Set(ref _input, value); }
    public string Keyword { get => _keyword; set => Set(ref _keyword, value); }
    public ObservableCollection<VersionRow> History { get; } = new();
    public VersionRow? Selected { get => _selected; set { if (Set(ref _selected, value)) OnPropertyChanged(nameof(Result)); } }
    public string Result => _selected?.Version.Content ?? "";
    public string Notice => "Keyword volumes and difficulty scores need a paid SEO data service and are not included. " +
                            "Keyword suggestions here are the SEO Specialist's judgement from search intent and live results.";

    public AsyncCommand RunCommand { get; }
    public RelayCommand CopyCommand { get; }

    public SeoToolsViewModel(MainViewModel main)
    {
        _main = main;
        RunCommand = new AsyncCommand(async () =>
        {
            var v = await Task.Run(() => _main.Services.Workflow.RunSeoToolAsync(Input, Keyword));
            Refresh();
            Selected = History.FirstOrDefault(h => h.Version.Id == v.Id);
        }, () => !string.IsNullOrWhiteSpace(Input));
        CopyCommand = new RelayCommand(() => UiHelpers.CopyToClipboard(Result), () => _selected != null);
    }

    public override void Refresh()
    {
        var keep = _selected?.Version.Id;
        History.Clear();
        foreach (var v in _main.Services.Repository.ListVersions(WorkflowOrchestrator.ToolsProjectId)
                     .Where(v => v.Kind == ArtifactKind.SeoToolReport).Reverse())
            History.Add(new VersionRow { Version = v, Label = v.Note ?? "SEO report", Meta = UiHelpers.Local(v.CreatedAt) });
        _selected = History.FirstOrDefault(h => h.Version.Id == keep) ?? History.FirstOrDefault();
        OnPropertyChanged(nameof(Selected));
        OnPropertyChanged(nameof(Result));
    }
}

// ============================================================ Social Media Content

public sealed class SocialMediaViewModel : SectionViewModel
{
    private readonly MainViewModel _main;
    private ProjectRow? _project;
    private VersionRow? _selected;

    public override string Title => "Social Media Content";
    public override string Icon => "";

    public ObservableCollection<ProjectRow> Projects { get; } = new();
    public ObservableCollection<VersionRow> Packs { get; } = new();
    public ProjectRow? Project { get => _project; set { if (Set(ref _project, value)) LoadPacks(); } }
    public VersionRow? Selected { get => _selected; set { if (Set(ref _selected, value)) OnPropertyChanged(nameof(Content)); } }
    public string Content => _selected?.Version.Content ?? "Choose a project and generate a social media pack from its final draft.";

    public AsyncCommand GenerateCommand { get; }
    public RelayCommand CopyCommand { get; }

    public SocialMediaViewModel(MainViewModel main)
    {
        _main = main;
        GenerateCommand = new AsyncCommand(async () =>
        {
            var p = _main.Services.Repository.GetProject(_project!.Id)!;
            if (p.Status != ProjectStatus.Approved &&
                !UiHelpers.Confirm("This content has not been approved by you yet. Generate social posts from the current draft anyway?")) return;
            var v = await Task.Run(() => _main.Services.Workflow.GenerateSocialPackAsync(_project.Id));
            LoadPacks();
            Selected = Packs.FirstOrDefault(x => x.Version.Id == v.Id);
        }, () => _project != null && !_main.Services.Workflow.IsRunning(_project.Id));
        CopyCommand = new RelayCommand(() => UiHelpers.CopyToClipboard(_selected?.Version.Content), () => _selected != null);
    }

    private void LoadPacks()
    {
        Packs.Clear();
        if (_project != null)
            foreach (var v in _main.Services.Repository.ListVersions(_project.Id).Where(v => v.Kind == ArtifactKind.SocialPack).Reverse())
                Packs.Add(new VersionRow { Version = v, Label = $"Social pack v{v.VersionNumber}", Meta = UiHelpers.Local(v.CreatedAt) + (v.Note != null ? " · " + v.Note : "") });
        Selected = Packs.FirstOrDefault();
    }

    public override void Refresh()
    {
        var keep = _project?.Id;
        Projects.Clear();
        foreach (var p in _main.Services.Repository.ListProjects().Where(p => p.CurrentDraftVersionId != null))
            Projects.Add(new ProjectRow { Id = p.Id, Title = p.Title, Status = MainViewModel.StatusText(p.Status) });
        _project = Projects.FirstOrDefault(p => p.Id == keep);
        OnPropertyChanged(nameof(Project));
        LoadPacks();
    }
}

// ============================================================ Editorial Review

public sealed class EditorialReviewViewModel : SectionViewModel
{
    private readonly MainViewModel _main;
    private ProjectRow? _project;
    private string _feedback = "";
    private string _draft = "", _report = "", _decisionInfo = "";
    private QualityReport? _quality;
    private Project? _model;

    public override string Title => "Editorial Review";
    public override string Icon => "";

    public ObservableCollection<ProjectRow> Queue { get; } = new();
    public ObservableCollection<CriterionScore> Scores { get; } = new();
    public ObservableCollection<QualityIssue> Issues { get; } = new();

    public ProjectRow? Project { get => _project; set { if (Set(ref _project, value)) LoadProject(); } }
    public string Draft { get => _draft; private set => Set(ref _draft, value); }
    public string ReportText { get => _report; private set => Set(ref _report, value); }
    public string DecisionInfo { get => _decisionInfo; private set => Set(ref _decisionInfo, value); }
    public string Feedback { get => _feedback; set => Set(ref _feedback, value); }
    public string OverallScore => _quality == null ? "—" : $"{_quality.Overall:0.0} / 10";
    public string QaDecisionText => _quality == null ? "No quality report yet" :
        _quality.Decision == QaDecision.Approve ? "Approved by Editorial Quality Manager" : "Not approved by Editorial Quality Manager";
    public string QaDecisionColor => _quality?.Decision == QaDecision.Approve ? "#059669" : "#DC2626";
    public string Disclaimer => QualityRules.Disclaimer;

    public RelayCommand ApproveCommand { get; }
    public RelayCommand RequestChangesCommand { get; }
    public RelayCommand RejectCommand { get; }
    public RelayCommand ExportCommand { get; }
    public RelayCommand CopyCommand { get; }

    public EditorialReviewViewModel(MainViewModel main)
    {
        _main = main;
        bool Awaiting() => _model?.Status == ProjectStatus.AwaitingApproval && !_main.Services.Workflow.IsRunning(_model.Id);

        ApproveCommand = new RelayCommand(() =>
        {
            if (!_model!.QaApproved && !UiHelpers.Confirm("The Editorial Quality Manager did NOT approve this content. Approve it anyway?")) return;
            _main.Services.Workflow.ApproveByUser(_model.Id, string.IsNullOrWhiteSpace(Feedback) ? null : Feedback);
            Feedback = "";
            Refresh();
        }, Awaiting);
        RequestChangesCommand = new RelayCommand(() =>
        {
            _main.Services.Workflow.RequestChanges(_model!.Id, Feedback);
            Feedback = "";
            _main.StartProject(_model.Id);
            Refresh();
        }, () => _model?.Status is ProjectStatus.AwaitingApproval or ProjectStatus.Approved
                 && !_main.Services.Workflow.IsRunning(_model.Id) && !string.IsNullOrWhiteSpace(Feedback));
        RejectCommand = new RelayCommand(() =>
        {
            if (!UiHelpers.Confirm("Reject this content?")) return;
            _main.Services.Workflow.Reject(_model!.Id, Feedback);
            Feedback = "";
            Refresh();
        }, Awaiting);
        ExportCommand = new RelayCommand(() => Exporter.Export(_model?.Title ?? "content", Draft), () => Draft.Length > 0);
        CopyCommand = new RelayCommand(() => UiHelpers.CopyToClipboard(Draft), () => Draft.Length > 0);
    }

    public void SelectProject(string id)
    {
        Refresh();
        Project = Queue.FirstOrDefault(p => p.Id == id);
    }

    public override void Refresh()
    {
        var keep = _project?.Id;
        Queue.Clear();
        foreach (var p in _main.Services.Repository.ListProjects()
                     .Where(p => p.LatestQualityReportVersionId != null || p.Status == ProjectStatus.AwaitingApproval)
                     .OrderBy(p => p.Status == ProjectStatus.AwaitingApproval ? 0 : 1).ThenByDescending(p => p.UpdatedAt))
        {
            Queue.Add(new ProjectRow
            {
                Id = p.Id, Title = p.Title, Status = MainViewModel.StatusText(p.Status),
                StatusColor = MainViewModel.StatusColor(p.Status), Updated = UiHelpers.Local(p.UpdatedAt), QaApproved = p.QaApproved,
            });
        }
        _project = Queue.FirstOrDefault(p => p.Id == keep) ?? Queue.FirstOrDefault();
        OnPropertyChanged(nameof(Project));
        LoadProject();
    }

    private void LoadProject()
    {
        Scores.Clear();
        Issues.Clear();
        _quality = null;
        _model = _project == null ? null : _main.Services.Repository.GetProject(_project.Id);
        if (_model == null)
        {
            Draft = ""; ReportText = ""; DecisionInfo = "";
        }
        else
        {
            var repo = _main.Services.Repository;
            Draft = _model.CurrentDraftVersionId != null ? repo.GetVersion(_model.Id, _model.CurrentDraftVersionId)?.Content ?? "" : "";
            var qa = _model.LatestQualityReportVersionId != null ? repo.GetVersion(_model.Id, _model.LatestQualityReportVersionId) : null;
            ReportText = qa?.Content ?? "";
            if (qa?.Data != null)
            {
                try { _quality = System.Text.Json.JsonSerializer.Deserialize<QualityReport>(qa.Data, Json.Options); }
                catch (System.Text.Json.JsonException) { _quality = null; }
            }
            if (_quality != null)
            {
                foreach (var s in _quality.Scores) Scores.Add(s);
                foreach (var i in _quality.Issues) Issues.Add(i);
            }
            var reviewedCurrent = qa != null && qa.ParentId == _model.CurrentDraftVersionId;
            DecisionInfo = MainViewModel.StatusText(_model.Status) +
                           (qa != null && !reviewedCurrent ? " · The current draft was changed after the last quality review." : "") +
                           (string.IsNullOrWhiteSpace(_model.UserDecisionNote) ? "" : " · Your note: " + _model.UserDecisionNote);
        }
        OnPropertyChanged(nameof(OverallScore));
        OnPropertyChanged(nameof(QaDecisionText));
        OnPropertyChanged(nameof(QaDecisionColor));
    }
}

public static class Exporter
{
    public static void Export(string title, string markdown)
    {
        var safe = string.Concat(title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        var dlg = new SaveFileDialog
        {
            FileName = safe.Length > 60 ? safe[..60] : safe,
            Filter = "Markdown (*.md)|*.md|Plain text (*.txt)|*.txt|Web page (*.html)|*.html",
        };
        if (dlg.ShowDialog() != true) return;
        var content = Path.GetExtension(dlg.FileName).ToLowerInvariant() switch
        {
            ".html" => MarkdownLite.ToHtml(title, markdown),
            _ => markdown,
        };
        File.WriteAllText(dlg.FileName, content);
    }
}
