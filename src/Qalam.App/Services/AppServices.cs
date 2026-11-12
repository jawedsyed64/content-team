using System.Net.Http;
using Qalam.Core.Agents;
using Qalam.Core.AI;
using Qalam.Core.Context;
using Qalam.Core.Documents;
using Qalam.Core.Models;
using Qalam.Core.Storage;
using Qalam.Core.Workflow;

namespace Qalam.App.Services;

public sealed class CalendarData
{
    public List<CalendarEntry> Entries { get; set; } = new();
    public List<string> Themes { get; set; } = new();
}

/// <summary>Composition root: creates every service once.</summary>
public sealed class AppServices
{
    public string DataFolder { get; }
    public JsonFileStore<AppSettings> SettingsStore { get; }
    public AppSettings Settings { get; private set; }
    public SecretStore Secrets { get; }
    public IProjectRepository Repository { get; }
    public ContextStore Context { get; }
    public BrandDocumentStore BrandDocs { get; }
    public JsonFileStore<CalendarData> Calendar { get; }
    public IAiClient Ai { get; }
    public AgentRunner Runner { get; }
    public AgentStatusTracker Tracker { get; }
    public WorkflowOrchestrator Workflow { get; }

    public AppServices()
    {
        DataFolder = Environment.GetEnvironmentVariable("QALAM_DATA")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Qalam");
        Directory.CreateDirectory(DataFolder);

        SettingsStore = new JsonFileStore<AppSettings>(Path.Combine(DataFolder, "settings.json"));
        Settings = SettingsStore.Load();
        Secrets = new SecretStore(DataFolder);
        Repository = new JsonProjectRepository(DataFolder);
        Context = new ContextStore(DataFolder);
        BrandDocs = new BrandDocumentStore(DataFolder);
        Calendar = new JsonFileStore<CalendarData>(Path.Combine(DataFolder, "calendar.json"));

        Ai = new ClaudeClient(new HttpClient { Timeout = TimeSpan.FromMinutes(10) }, Secrets.GetApiKey);
        Runner = new AgentRunner(Ai, Context, BrandDocs, () => Settings);
        Tracker = new AgentStatusTracker();
        Workflow = new WorkflowOrchestrator(Repository, Runner, Context, () => Settings, Tracker);
    }

    public void SaveSettings(AppSettings settings)
    {
        Settings = settings;
        SettingsStore.Save(settings);
    }
}
