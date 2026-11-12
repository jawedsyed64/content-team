using Microsoft.Win32;
using Qalam.Core.AI;
using Qalam.Core.Context;
using Qalam.Core.Documents;
using Qalam.Core.Models;

namespace Qalam.App.ViewModels;

public sealed class ContextFileRow
{
    public ContextFileKind Kind { get; init; }
    public string Name { get; init; } = "";
    public string State { get; init; } = "";
    public string Color { get; init; } = "";
}

public sealed class BrandGuidelinesViewModel : SectionViewModel
{
    private readonly MainViewModel _main;
    private ContextFileRow? _selected;
    private string _text = "";
    private bool _dirty;
    private BrandDocument? _selectedDoc;

    public override string Title => "Brand Guidelines";
    public override string Icon => "";

    public ObservableCollection<ContextFileRow> Files { get; } = new();
    public ObservableCollection<BrandDocument> Documents { get; } = new();

    public ContextFileRow? Selected
    {
        get => _selected;
        set
        {
            if (_dirty && value?.Kind != _selected?.Kind && !UiHelpers.Confirm("Discard unsaved changes to this file?"))
            {
                OnPropertyChanged();
                return;
            }
            if (Set(ref _selected, value)) LoadText();
        }
    }

    public string Text
    {
        get => _text;
        set
        {
            if (!Set(ref _text, value)) return;
            _dirty = true;
            OnPropertyChanged(nameof(EditorStatus));
        }
    }

    public string EditorStatus => _selected == null ? "" :
        $"{ContextValidator.CountPlaceholders(_text)} [FILL IN] item(s) remaining" + (_dirty ? " · unsaved changes" : "");

    public BrandDocument? SelectedDocument { get => _selectedDoc; set => Set(ref _selectedDoc, value); }

    public RelayCommand SaveCommand { get; }
    public RelayCommand ResetCommand { get; }
    public RelayCommand UploadCommand { get; }
    public RelayCommand RemoveDocCommand { get; }
    public RelayCommand OpenFolderCommand { get; }

    public BrandGuidelinesViewModel(MainViewModel main)
    {
        _main = main;
        SaveCommand = new RelayCommand(() =>
        {
            _main.Services.Context.Write(_selected!.Kind, _text);
            _dirty = false;
            Refresh();
        }, () => _selected != null && _dirty);
        ResetCommand = new RelayCommand(() =>
        {
            if (!UiHelpers.Confirm("Replace this file with the blank template? The current text is kept in history.")) return;
            _main.Services.Context.ResetToTemplate(_selected!.Kind);
            _dirty = false;
            LoadText();
            Refresh();
        }, () => _selected != null);
        UploadCommand = new RelayCommand(() =>
        {
            var dlg = new OpenFileDialog
            {
                Multiselect = true,
                Filter = "Brand documents (*.txt;*.md;*.docx)|*.txt;*.md;*.markdown;*.docx",
            };
            if (dlg.ShowDialog() != true) return;
            foreach (var f in dlg.FileNames) _main.Services.BrandDocs.Import(f);
            Refresh();
        });
        RemoveDocCommand = new RelayCommand(() =>
        {
            if (!UiHelpers.Confirm($"Remove {_selectedDoc!.FileName}?")) return;
            _main.Services.BrandDocs.Remove(_selectedDoc);
            Refresh();
        }, () => _selectedDoc != null);
        OpenFolderCommand = new RelayCommand(() =>
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_main.Services.Context.Folder) { UseShellExecute = true }));
    }

    private void LoadText()
    {
        _text = _selected == null ? "" : _main.Services.Context.Read(_selected.Kind);
        _dirty = false;
        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(EditorStatus));
    }

    public override void Refresh()
    {
        var keep = _selected?.Kind;
        Files.Clear();
        foreach (var kind in ContextFiles.All)
        {
            var count = ContextValidator.CountPlaceholders(_main.Services.Context.Read(kind));
            Files.Add(new ContextFileRow
            {
                Kind = kind,
                Name = ContextFiles.DisplayName(kind),
                State = count == 0 ? "Complete" : $"{count} to fill in",
                Color = count == 0 ? "#059669" : "#D97706",
            });
        }
        var target = Files.FirstOrDefault(f => f.Kind == keep) ?? Files.First();
        if (_selected?.Kind != target.Kind || !_dirty)
        {
            _selected = target;
            if (!_dirty) LoadText();
        }
        else _selected = target;
        OnPropertyChanged(nameof(Selected));

        Documents.Clear();
        foreach (var d in _main.Services.BrandDocs.List()) Documents.Add(d);
    }
}

public sealed class SettingsViewModel : SectionViewModel
{
    private readonly MainViewModel _main;
    private string _model = "", _reviewModel = "", _maxTokens = "", _maxRevisions = "", _maxSearches = "", _maxDocChars = "";
    private bool _webSearch, _strict;
    private string _testResult = "";

    public override string Title => "Settings";
    public override string Icon => "";

    public IReadOnlyList<string> Models => AppSettings.KnownModels;
    public string Model { get => _model; set => Set(ref _model, value); }
    public string ReviewModel { get => _reviewModel; set => Set(ref _reviewModel, value); }
    public string MaxTokens { get => _maxTokens; set => Set(ref _maxTokens, value); }
    public string MaxRevisions { get => _maxRevisions; set => Set(ref _maxRevisions, value); }
    public string MaxSearches { get => _maxSearches; set => Set(ref _maxSearches, value); }
    public string MaxDocChars { get => _maxDocChars; set => Set(ref _maxDocChars, value); }
    public bool WebSearch { get => _webSearch; set => Set(ref _webSearch, value); }
    public bool StrictContext { get => _strict; set => Set(ref _strict, value); }
    public string ApiKeyStatus => SecretStore_Mask();
    public string TestResult { get => _testResult; private set => Set(ref _testResult, value); }
    public string DataFolder => _main.Services.DataFolder;

    public RelayCommand SaveCommand { get; }
    public RelayCommand SaveKeyCommand { get; }
    public RelayCommand ClearKeyCommand { get; }
    public AsyncCommand TestCommand { get; }
    public RelayCommand OpenDataCommand { get; }

    public SettingsViewModel(MainViewModel main)
    {
        _main = main;
        SaveCommand = new RelayCommand(Save);
        SaveKeyCommand = new RelayCommand(p =>
        {
            if (p is not System.Windows.Controls.PasswordBox box || string.IsNullOrWhiteSpace(box.Password)) return;
            if (!box.Password.Trim().StartsWith("sk-ant-") &&
                !UiHelpers.Confirm("This doesn't look like an Anthropic API key (they start with \"sk-ant-\"). Save it anyway?")) return;
            _main.Services.Secrets.SetApiKey(box.Password);
            box.Clear();
            OnPropertyChanged(nameof(ApiKeyStatus));
            _main.SettingsChanged();
        });
        ClearKeyCommand = new RelayCommand(() =>
        {
            if (!UiHelpers.Confirm("Remove the saved API key from this computer?")) return;
            _main.Services.Secrets.SetApiKey(null);
            OnPropertyChanged(nameof(ApiKeyStatus));
            _main.SettingsChanged();
        });
        TestCommand = new AsyncCommand(TestAsync);
        OpenDataCommand = new RelayCommand(() =>
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(DataFolder) { UseShellExecute = true }));
    }

    private string SecretStore_Mask() => Services.SecretStore.Mask(_main.Services.Secrets.GetApiKey());

    public override void Refresh()
    {
        var s = _main.Services.Settings;
        Model = s.Model;
        ReviewModel = s.ReviewModel ?? "";
        MaxTokens = s.MaxTokensPerStage.ToString();
        MaxRevisions = s.MaxRevisionRounds.ToString();
        MaxSearches = s.MaxSearchesPerStage.ToString();
        MaxDocChars = s.MaxBrandDocChars.ToString();
        WebSearch = s.EnableWebSearch;
        StrictContext = s.StrictContext;
        OnPropertyChanged(nameof(ApiKeyStatus));
    }

    private void Save()
    {
        static int Parse(string v, int min, int max, string name) =>
            int.TryParse(v, out var n) && n >= min && n <= max ? n
                : throw new ArgumentException($"{name} must be a number between {min} and {max}.");

        var settings = new AppSettings
        {
            Model = string.IsNullOrWhiteSpace(Model) ? AppSettings.KnownModels[0] : Model.Trim(),
            ReviewModel = string.IsNullOrWhiteSpace(ReviewModel) ? null : ReviewModel.Trim(),
            MaxTokensPerStage = Parse(MaxTokens, 1000, 64000, "Max tokens per stage"),
            MaxRevisionRounds = Parse(MaxRevisions, 0, 5, "Max revision rounds"),
            MaxSearchesPerStage = Parse(MaxSearches, 1, 20, "Max web searches per stage"),
            MaxBrandDocChars = Parse(MaxDocChars, 0, 200000, "Brand document characters"),
            EnableWebSearch = WebSearch,
            StrictContext = StrictContext,
        };
        _main.Services.SaveSettings(settings);
        _main.SettingsChanged();
        TestResult = "Settings saved.";
    }

    private async Task TestAsync()
    {
        TestResult = "Testing…";
        try
        {
            var response = await _main.Services.Ai.CompleteAsync(new AiRequest
            {
                Model = string.IsNullOrWhiteSpace(Model) ? _main.Services.Settings.Model : Model.Trim(),
                System = "Reply with the single word OK.",
                UserMessage = "Connection test",
                MaxTokens = 16,
            }, CancellationToken.None);
            TestResult = $"Connected. Model replied: \"{response.Text}\" ({response.Usage.InputTokens + response.Usage.OutputTokens} tokens).";
        }
        catch (AiException ex)
        {
            TestResult = "Failed: " + ex.UserMessage;
        }
    }
}
