using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Qalam.Core.Agents;
using Qalam.Core.AI;
using Qalam.Core.Context;
using Qalam.Core.Models;
using Qalam.Core.Quality;
using Qalam.Core.Storage;
using static Qalam.Core.Workflow.StagePrompts;

namespace Qalam.Core.Workflow;

public sealed class ContextMissingException : Exception
{
    public IReadOnlyList<MissingContextItem> Missing { get; }
    public ContextMissingException(IReadOnlyList<MissingContextItem> missing)
        : base("Some context files still need to be filled in: " + string.Join("; ", missing.Take(5)))
        => Missing = missing;
}

public sealed record CalendarResult(List<CalendarEntry> Entries, List<string> Themes, List<string> Questions, ArtifactVersion Version);

/// <summary>
/// Runs the 8-stage content workflow. Each stage invokes one specialised agent through its own prompt;
/// every output is saved as a new version and every hand-off is recorded as an agent message.
/// </summary>
public sealed class WorkflowOrchestrator
{
    public const string ToolsProjectId = "_tools";
    public const string UserName = "You";

    private readonly IProjectRepository _repo;
    private readonly IAgentRunner _runner;
    private readonly ContextStore _context;
    private readonly Func<AppSettings> _settings;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new();
    private readonly ConcurrentDictionary<string, bool> _pauseRequested = new();

    public AgentStatusTracker Tracker { get; }

    /// <summary>Raised (on a background thread) whenever a project is saved.</summary>
    public event EventHandler<string>? ProjectChanged;

    public WorkflowOrchestrator(IProjectRepository repo, IAgentRunner runner, ContextStore context,
        Func<AppSettings> settings, AgentStatusTracker? tracker = null)
    {
        _repo = repo;
        _runner = runner;
        _context = context;
        _settings = settings;
        Tracker = tracker ?? new AgentStatusTracker();
    }

    public bool IsRunning(string projectId) => _running.ContainsKey(projectId);

    // ===================================================================== project lifecycle

    public Project CreateProject(ContentBrief brief, string? title = null)
    {
        if (string.IsNullOrWhiteSpace(brief.Topic)) throw new ArgumentException("A topic is required.");
        var p = new Project
        {
            Title = string.IsNullOrWhiteSpace(title) ? brief.Topic.Trim() : title.Trim(),
            Brief = brief,
            Stages = Project.DefaultStages(),
        };
        Log(p, ActivityLevel.Info, null, "Project created.");
        Message(p, UserName, AgentCatalog.Strategist.Name, MessageType.Brief,
            $"New brief: \"{brief.Topic}\" ({FormatName(brief.Format)}).");
        Save(p);
        return p;
    }

    /// <summary>Starts or resumes the workflow. Returns when the project finishes, pauses, needs input or fails.</summary>
    public async Task RunAsync(string projectId, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (!_running.TryAdd(projectId, cts))
            throw new InvalidOperationException("This project is already running.");
        _pauseRequested.TryRemove(projectId, out _);

        Project? p = null;
        try
        {
            p = Load(projectId);
            if (p.Status is ProjectStatus.Approved or ProjectStatus.Rejected or ProjectStatus.AwaitingApproval) return;

            foreach (var s in p.Stages.Where(s => s.Status is StageStatus.Failed or StageStatus.Incomplete or StageStatus.Running))
            {
                s.Status = StageStatus.Pending;
                s.Error = null;
            }
            if (p.Stages.Any(s => s.Status == StageStatus.NeedsInput))
            {
                p.Status = ProjectStatus.NeedsInput;
                Save(p);
                return;
            }

            Log(p, ActivityLevel.Info, null, p.Status == ProjectStatus.NotStarted ? "Workflow started." : "Workflow resumed.");
            p.Status = ProjectStatus.Running;
            p.StatusDetail = null;
            Save(p);

            while (true)
            {
                var stage = p.Stages.FirstOrDefault(s => s.Status == StageStatus.Pending);
                if (stage == null) break;

                if (_pauseRequested.ContainsKey(projectId))
                {
                    p.Status = ProjectStatus.Paused;
                    p.StatusDetail = "Paused by you. Resume to continue with " + StageTitle(stage.Kind) + ".";
                    Log(p, ActivityLevel.Info, null, "Workflow paused.");
                    Save(p);
                    return;
                }
                cts.Token.ThrowIfCancellationRequested();

                if (stage.Kind == StageKind.Delivery)
                {
                    Deliver(p, stage);
                    Save(p);
                    return;
                }

                var agent = AgentCatalog.Get(stage.Agent!.Value);
                if (!ContextReady(p, stage, agent))
                {
                    Save(p);
                    return;
                }

                var keepGoing = await RunStageAsync(p, stage, agent, cts.Token).ConfigureAwait(false);
                Save(p);
                if (!keepGoing) return;
            }
        }
        catch (OperationCanceledException)
        {
            if (p != null)
            {
                foreach (var s in p.Stages.Where(s => s.Status == StageStatus.Running)) s.Status = StageStatus.Pending;
                p.Status = ProjectStatus.Paused;
                p.StatusDetail = "Stopped by you. Resume to retry the current stage.";
                Log(p, ActivityLevel.Warning, null, "Workflow stopped; the in-progress stage will be retried on resume.");
                Save(p);
            }
        }
        finally
        {
            _running.TryRemove(projectId, out _);
            _pauseRequested.TryRemove(projectId, out _);
        }
    }

    /// <summary>Pause after the current stage finishes.</summary>
    public void RequestPause(string projectId)
    {
        if (_running.ContainsKey(projectId)) _pauseRequested[projectId] = true;
    }

    /// <summary>Abort immediately, cancelling the in-flight API call.</summary>
    public void Stop(string projectId)
    {
        if (_running.TryGetValue(projectId, out var cts)) cts.Cancel();
    }

    public void AnswerQuestions(string projectId, string answer)
    {
        var p = LoadIdle(projectId);
        var stage = p.Stages.FirstOrDefault(s => s.Status == StageStatus.NeedsInput)
            ?? throw new InvalidOperationException("No agent is waiting for an answer.");
        var agentName = stage.Agent is { } a ? AgentCatalog.Get(a).Name : "Team";

        var text = string.IsNullOrWhiteSpace(answer)
            ? "No further information is available. Proceed with what you have, and clearly mark anything unknown instead of guessing."
            : answer.Trim();
        p.Answers.Add(new QuestionAnswer { Stage = stage.Kind, Questions = stage.Questions.ToList(), Answer = text });
        if (stage.BlockedByContext) p.AllowIncompleteContext = true;

        Message(p, UserName, agentName, MessageType.UserAnswer, Shorten(text, 160));
        Log(p, ActivityLevel.Info, stage.Agent, "You answered the agent's questions.");
        stage.Status = StageStatus.Pending;
        stage.Questions.Clear();
        stage.BlockedByContext = false;
        p.Status = ProjectStatus.Paused;
        p.StatusDetail = "Answer received — resume to continue.";
        Save(p);
    }

    public void ApproveByUser(string projectId, string? note = null)
    {
        var p = LoadIdle(projectId);
        if (p.Status != ProjectStatus.AwaitingApproval)
            throw new InvalidOperationException("The project is not waiting for your approval.");
        p.Status = ProjectStatus.Approved;
        p.UserDecisionNote = note;
        p.StatusDetail = "Approved by you.";
        Message(p, UserName, "Editorial team", MessageType.UserDecision, "Approved." + (string.IsNullOrWhiteSpace(note) ? "" : " " + note));
        Log(p, p.QaApproved ? ActivityLevel.Info : ActivityLevel.Warning, null,
            p.QaApproved ? "Final content approved by you." : "Approved by you although the Editorial Quality Manager did not approve it.");
        Save(p);
    }

    /// <summary>Your feedback goes to the Writer; the draft then passes SEO, editing and QA again.</summary>
    public void RequestChanges(string projectId, string feedback)
    {
        if (string.IsNullOrWhiteSpace(feedback)) throw new ArgumentException("Please describe the changes you want.");
        var p = LoadIdle(projectId);
        if (p.Status is not (ProjectStatus.AwaitingApproval or ProjectStatus.Approved))
            throw new InvalidOperationException("You can request changes once the content has been delivered.");
        p.RevisionRound++;
        p.QaRevisionsInCycle = 0;
        p.QaApproved = false;
        p.PendingRevisionRequests = new List<string> { "Editor-in-chief (user) feedback: " + feedback.Trim() };
        ResetStages(p, StageKind.Draft);
        p.Status = ProjectStatus.Paused;
        p.StatusDetail = "Changes requested — resume to send them to the Writer.";
        Message(p, UserName, AgentCatalog.Writer.Name, MessageType.UserFeedback, Shorten(feedback, 200));
        Log(p, ActivityLevel.Info, null, $"You requested changes (revision round {p.RevisionRound}).");
        Save(p);
    }

    public void Reject(string projectId, string? note = null)
    {
        var p = LoadIdle(projectId);
        p.Status = ProjectStatus.Rejected;
        p.UserDecisionNote = note;
        p.StatusDetail = "Rejected by you.";
        Message(p, UserName, "Editorial team", MessageType.UserDecision, "Rejected." + (string.IsNullOrWhiteSpace(note) ? "" : " " + note));
        Log(p, ActivityLevel.Info, null, "Project rejected by you.");
        Save(p);
    }

    /// <summary>Saves your manual edit as a new version (earlier versions are kept).</summary>
    public ArtifactVersion SaveUserEdit(string projectId, string content, string? note = null)
    {
        var p = LoadIdle(projectId);
        var v = AddVersion(p, ArtifactKind.Draft, UserName, null, content, p.CurrentDraftVersionId,
            note: note ?? "Manual edit in the Writing Workspace");
        p.CurrentDraftVersionId = v.Id;
        if (p.QaApproved || p.Status is ProjectStatus.AwaitingApproval or ProjectStatus.Approved)
        {
            p.QaApproved = false;
            Log(p, ActivityLevel.Warning, null, "You edited the draft. The previous quality review no longer covers this text — run a QA re-check.");
            if (p.Status == ProjectStatus.Approved) p.Status = ProjectStatus.AwaitingApproval;
        }
        Message(p, UserName, "Editorial team", MessageType.Draft, $"Manual edit saved as draft v{v.VersionNumber}.", v.Id);
        Save(p);
        return v;
    }

    /// <summary>Sends the current draft back through the Editorial Quality Manager.</summary>
    public void RequestQualityRecheck(string projectId)
    {
        var p = LoadIdle(projectId);
        if (p.CurrentDraftVersionId == null) throw new InvalidOperationException("There is no draft to check yet.");
        p.QaRevisionsInCycle = 0;
        ResetStages(p, StageKind.QualityAssurance);
        p.Status = ProjectStatus.Paused;
        p.StatusDetail = "QA re-check queued — resume to run it.";
        Log(p, ActivityLevel.Info, null, "QA re-check requested.");
        Save(p);
    }

    public void DeleteProject(string projectId)
    {
        if (IsRunning(projectId)) throw new InvalidOperationException("Stop the project before deleting it.");
        _repo.DeleteProject(projectId);
        ProjectChanged?.Invoke(this, projectId);
    }

    // ===================================================================== stages

    private bool ContextReady(Project p, StageState stage, AgentDefinition agent)
    {
        if (!_settings().StrictContext || p.AllowIncompleteContext) return true;
        var missing = ContextValidator.FindMissing(_context, agent.ContextFiles);
        if (missing.Count == 0) return true;

        stage.Status = StageStatus.NeedsInput;
        stage.BlockedByContext = true;
        stage.Questions = missing.Select(m => $"Please provide — {m}").ToList();
        p.Status = ProjectStatus.NeedsInput;
        p.StatusDetail = $"{agent.Name} needs information that is missing from your Brand Guidelines ({missing.Count} item(s)). " +
                         "Fill them in under Brand Guidelines, or answer here to continue with this project only.";
        Message(p, agent.Name, UserName, MessageType.Question, $"{missing.Count} context item(s) missing before I can start.");
        Log(p, ActivityLevel.Warning, agent.Id, $"Stopped before {StageTitle(stage.Kind)}: context files are incomplete.");
        return false;
    }

    private async Task<bool> RunStageAsync(Project p, StageState stage, AgentDefinition agent, CancellationToken ct)
    {
        stage.Status = StageStatus.Running;
        stage.StartedAt = DateTime.UtcNow;
        stage.Attempts++;
        stage.Error = null;
        p.StatusDetail = $"{agent.Name}: {StageTitle(stage.Kind)}…";
        Log(p, ActivityLevel.Info, agent.Id, $"{agent.Name} started {StageTitle(stage.Kind)}" +
            (p.RevisionRound > 0 && stage.Kind >= StageKind.Draft ? $" (revision round {p.RevisionRound})." : "."));
        Save(p);
        Tracker.Started(agent.Id, $"{StageTitle(stage.Kind)} — {p.Title}", p.Id);

        try
        {
            var ok = stage.Kind switch
            {
                StageKind.Strategy => await StrategyAsync(p, stage, ct),
                StageKind.Research => await ResearchAsync(p, stage, ct),
                StageKind.Planning => await PlanningAsync(p, stage, ct),
                StageKind.Draft => await DraftAsync(p, stage, ct),
                StageKind.Seo => await SeoAsync(p, stage, ct),
                StageKind.Editing => await EditingAsync(p, stage, ct),
                StageKind.QualityAssurance => await QualityAsync(p, stage, ct),
                _ => throw new InvalidOperationException("Unknown stage " + stage.Kind)
            };
            if (stage.Status == StageStatus.Running)
            {
                stage.Status = StageStatus.Completed;
                stage.CompletedAt = DateTime.UtcNow;
            }
            if (stage.Status is StageStatus.Completed or StageStatus.Pending)
                Log(p, ActivityLevel.Info, agent.Id, $"{agent.Name} finished {StageTitle(stage.Kind)}.");
            Tracker.Finished(agent.Id);
            return ok;
        }
        catch (OperationCanceledException)
        {
            stage.Status = StageStatus.Pending;
            Tracker.Finished(agent.Id);
            throw;
        }
        catch (AiException ex)
        {
            Fail(p, stage, agent, ex.UserMessage);
            return false;
        }
        catch (Exception ex)
        {
            Fail(p, stage, agent, "Unexpected error: " + ex.Message);
            return false;
        }
    }

    private void Fail(Project p, StageState stage, AgentDefinition agent, string error)
    {
        stage.Status = StageStatus.Failed;
        stage.Error = error;
        p.Status = ProjectStatus.Failed;
        p.StatusDetail = $"{StageTitle(stage.Kind)} failed: {error} Resume to retry.";
        Log(p, ActivityLevel.Error, agent.Id, $"{agent.Name} failed: {error}");
        Tracker.Failed(agent.Id, error);
    }

    /// <summary>Calls the agent and handles the two common stop conditions: truncated output and questions for the user.</summary>
    private async Task<AgentResult?> CallAsync(Project p, StageState stage, AgentTask task, ArtifactKind kind, CancellationToken ct)
    {
        var agent = AgentCatalog.Get(task.Agent);
        var withAnswers = new AgentTask
        {
            Agent = task.Agent,
            OutputFormat = task.OutputFormat,
            WebSearchOverride = task.WebSearchOverride,
            Prompt = task.Prompt + "\n" + AnswersBlock(p),
        };
        var result = await _runner.RunAsync(withAnswers, ct).ConfigureAwait(false);

        if (!result.IsComplete)
        {
            var v = AddVersion(p, kind, agent.Name, agent.Id, result.Text, null, result.Response,
                note: "Incomplete — the response reached the token limit.", incomplete: true);
            stage.OutputVersionId = v.Id;
            stage.Status = StageStatus.Incomplete;
            stage.Error = "The response was cut off at the token limit.";
            p.Status = ProjectStatus.Failed;
            p.StatusDetail = $"{agent.Name}'s response was cut off. Increase 'Max tokens per stage' in Settings and resume.";
            Log(p, ActivityLevel.Warning, agent.Id, "Response incomplete (token limit). Partial output kept as a version.");
            Tracker.Failed(agent.Id, "Incomplete response");
            return null;
        }

        if (result.Questions.Count > 0)
        {
            if (!string.IsNullOrWhiteSpace(result.Text))
            {
                var v = AddVersion(p, kind, agent.Name, agent.Id, result.Text, null, result.Response,
                    note: "Provisional — the agent asked questions before finishing.");
                stage.OutputVersionId = v.Id;
            }
            stage.Status = StageStatus.NeedsInput;
            stage.Questions = result.Questions;
            p.Status = ProjectStatus.NeedsInput;
            p.StatusDetail = $"{agent.Name} needs your input ({result.Questions.Count} question(s)).";
            Message(p, agent.Name, UserName, MessageType.Question, string.Join(" | ", result.Questions.Take(3)));
            Log(p, ActivityLevel.Info, agent.Id, $"{agent.Name} asked you {result.Questions.Count} question(s).");
            return null;
        }

        return result;
    }

    private async Task<bool> StrategyAsync(Project p, StageState stage, CancellationToken ct)
    {
        var r = await CallAsync(p, stage, new AgentTask
        {
            Agent = AgentId.Strategist,
            OutputFormat = StrategyFormat,
            WebSearchOverride = false,
            Prompt = BriefBlock(p) + "\nTask: Evaluate this topic against our strategy and audience, and define the content objective for the team.",
        }, ArtifactKind.Objective, ct);
        if (r == null) return false;

        var v = AddVersion(p, ArtifactKind.Objective, r, null);
        stage.OutputVersionId = v.Id;
        Message(p, AgentCatalog.Strategist.Name, AgentCatalog.ResearchAnalyst.Name, MessageType.Objective,
            "Content objective defined; research questions attached.", v.Id);
        return true;
    }

    private async Task<bool> ResearchAsync(Project p, StageState stage, CancellationToken ct)
    {
        var objective = Output(p, StageKind.Strategy);
        var web = _settings().EnableWebSearch;
        var r = await CallAsync(p, stage, new AgentTask
        {
            Agent = AgentId.ResearchAnalyst,
            OutputFormat = ResearchFormat,
            Prompt = BriefBlock(p) + Input("Content Strategist — content objective", objective?.Content) +
                     "\nTask: Research this topic and prepare a structured research brief for the writer." +
                     (web ? "" : "\nWeb search is switched off: list what needs to be researched and do not present any figure as verified."),
        }, ArtifactKind.ResearchBrief, ct);
        if (r == null) return false;

        var v = AddVersion(p, ArtifactKind.ResearchBrief, r, objective?.Id);
        stage.OutputVersionId = v.Id;

        if (!web)
            Log(p, ActivityLevel.Warning, AgentId.ResearchAnalyst, "Web search is off — the research brief is not source-verified.");
        else if (r.Response.Citations.Count == 0 && r.Response.SearchResults.Count == 0 && CountLinks(r.Text) == 0)
            Log(p, ActivityLevel.Warning, AgentId.ResearchAnalyst, "No web sources were retrieved. Unsourced claims will be blocked at QA.");

        Message(p, AgentCatalog.ResearchAnalyst.Name, AgentCatalog.Ideation.Name, MessageType.ResearchBrief,
            $"Research brief ready ({Math.Max(v.Sources.Count, CountLinks(v.Content))} sources).", v.Id);
        Message(p, AgentCatalog.ResearchAnalyst.Name, AgentCatalog.Writer.Name, MessageType.ResearchBrief,
            "Research brief shared for drafting.", v.Id);
        return true;
    }

    private async Task<bool> PlanningAsync(Project p, StageState stage, CancellationToken ct)
    {
        var research = Output(p, StageKind.Research);
        var r = await CallAsync(p, stage, new AgentTask
        {
            Agent = AgentId.Ideation,
            OutputFormat = PlanningFormat,
            Prompt = BriefBlock(p) +
                     Input("Content Strategist — content objective", Output(p, StageKind.Strategy)?.Content) +
                     Input("Research Analyst — research brief", research?.Content) +
                     "\nTask: Develop headlines, the angle, hooks and a detailed outline for the writer.",
        }, ArtifactKind.ContentPlan, ct);
        if (r == null) return false;

        var v = AddVersion(p, ArtifactKind.ContentPlan, r, research?.Id);
        stage.OutputVersionId = v.Id;
        Message(p, AgentCatalog.Ideation.Name, AgentCatalog.Writer.Name, MessageType.ContentPlan,
            "Headlines, angle and outline ready.", v.Id);
        return true;
    }

    private async Task<bool> DraftAsync(Project p, StageState stage, CancellationToken ct)
    {
        var research = Output(p, StageKind.Research);
        var current = p.CurrentDraftVersionId != null ? _repo.GetVersion(p.Id, p.CurrentDraftVersionId) : null;
        var revising = current != null && p.PendingRevisionRequests.Count > 0;

        var prompt = BriefBlock(p) +
                     Input("Content Strategist — content objective", Output(p, StageKind.Strategy)?.Content) +
                     Input("Research Analyst — research brief (the ONLY permitted source of facts)", research?.Content) +
                     Input("Verified source links captured during research", research != null ? SourcesList(research.Sources) : null) +
                     Input("Content Ideation Specialist — content plan", Output(p, StageKind.Planning)?.Content);
        if (revising)
        {
            prompt += Input("Previous draft", current!.Content) +
                      Input("Revision requests", string.Join("\n", p.PendingRevisionRequests.Select((x, i) => $"{i + 1}. {x}"))) +
                      "\nTask: Revise the previous draft so that it addresses every revision request. Keep what already works. " +
                      "Remove any claim you cannot support from the research brief.";
        }
        else
        {
            prompt += "\nTask: Write the first draft following the content plan, the brand voice and the style guide.";
        }

        var r = await CallAsync(p, stage, new AgentTask { Agent = AgentId.Writer, OutputFormat = DraftFormat(p), Prompt = prompt },
            ArtifactKind.Draft, ct);
        if (r == null) return false;

        var v = AddVersion(p, ArtifactKind.Draft, r, current?.Id,
            note: revising ? $"Revision (round {p.RevisionRound})" : "First draft");
        stage.OutputVersionId = v.Id;
        p.CurrentDraftVersionId = v.Id;
        p.PendingRevisionRequests.Clear();
        Message(p, AgentCatalog.Writer.Name, AgentCatalog.Seo.Name, MessageType.Draft,
            $"{(revising ? "Revised" : "First")} draft v{v.VersionNumber} submitted ({WordCount(v.Content)} words).", v.Id);
        return true;
    }

    private async Task<bool> SeoAsync(Project p, StageState stage, CancellationToken ct)
    {
        var draft = CurrentDraft(p);
        var r = await CallAsync(p, stage, new AgentTask
        {
            Agent = AgentId.Seo,
            OutputFormat = SeoFormat,
            Prompt = BriefBlock(p) + Input("Content Writer — draft", draft.Content) +
                     "\nTask: Review this draft for search, recommend improvements, prepare meta data, and return the optimised draft.",
        }, ArtifactKind.SeoReport, ct);
        if (r == null) return false;

        var parts = AgentOutput.SplitSections(r.Text);
        var report = parts.GetValueOrDefault("SEO REPORT") ?? r.Text;
        var reportVersion = AddVersion(p, ArtifactKind.SeoReport, r, draft.Id, contentOverride: report);
        stage.OutputVersionId = reportVersion.Id;

        var optimised = parts.GetValueOrDefault("OPTIMIZED DRAFT");
        if (AcceptRevisedDraft(p, AgentId.Seo, draft.Content, optimised))
        {
            var dv = AddVersion(p, ArtifactKind.Draft, AgentCatalog.Seo.Name, AgentId.Seo, optimised!, draft.Id, null,
                note: "SEO-optimised");
            p.CurrentDraftVersionId = dv.Id;
            Message(p, AgentCatalog.Seo.Name, AgentCatalog.CopyEditor.Name, MessageType.Recommendations,
                $"SEO recommendations and meta data ready; optimised draft v{dv.VersionNumber}.", dv.Id);
        }
        else
        {
            Message(p, AgentCatalog.Seo.Name, AgentCatalog.CopyEditor.Name, MessageType.Recommendations,
                "SEO recommendations ready; draft passed on unchanged.", reportVersion.Id);
        }
        return true;
    }

    private async Task<bool> EditingAsync(Project p, StageState stage, CancellationToken ct)
    {
        var draft = CurrentDraft(p);
        var r = await CallAsync(p, stage, new AgentTask
        {
            Agent = AgentId.CopyEditor,
            OutputFormat = EditingFormat,
            Prompt = BriefBlock(p) + Input("Draft to edit", draft.Content) +
                     "\nTask: Copy-edit and proofread this draft. Improve grammar, readability, structure and flow without changing its meaning or facts.",
        }, ArtifactKind.EditLog, ct);
        if (r == null) return false;

        var parts = AgentOutput.SplitSections(r.Text);
        var edited = parts.GetValueOrDefault("EDITED DRAFT");
        var log = parts.GetValueOrDefault("CHANGE LOG") ?? "(no change log returned)";
        var logVersion = AddVersion(p, ArtifactKind.EditLog, r, draft.Id, contentOverride: log);
        stage.OutputVersionId = logVersion.Id;

        if (AcceptRevisedDraft(p, AgentId.CopyEditor, draft.Content, edited))
        {
            var dv = AddVersion(p, ArtifactKind.Draft, AgentCatalog.CopyEditor.Name, AgentId.CopyEditor, edited!, draft.Id, null,
                note: "Copy-edited");
            p.CurrentDraftVersionId = dv.Id;
            Message(p, AgentCatalog.CopyEditor.Name, AgentCatalog.QualityManager.Name, MessageType.EditedDraft,
                $"Edited draft v{dv.VersionNumber} submitted for final review.", dv.Id);
        }
        else
        {
            Message(p, AgentCatalog.CopyEditor.Name, AgentCatalog.QualityManager.Name, MessageType.EditedDraft,
                "Edit could not be applied; previous draft submitted for review.", logVersion.Id);
        }
        return true;
    }

    private async Task<bool> QualityAsync(Project p, StageState stage, CancellationToken ct)
    {
        var settings = _settings();
        var draft = CurrentDraft(p);
        var research = Output(p, StageKind.Research);
        var seo = LatestVersion(p, ArtifactKind.SeoReport);
        var previousQa = p.LatestQualityReportVersionId != null ? _repo.GetVersion(p.Id, p.LatestQualityReportVersionId) : null;

        var prompt = BriefBlock(p) +
                     Input("Research Analyst — research brief", research?.Content) +
                     Input("Verified source links captured during research", research != null ? SourcesList(research.Sources) : null) +
                     Input("SEO Specialist — report", seo?.Content) +
                     (previousQa != null ? Input("Your previous quality report (check whether its issues are now fixed)", previousQa.Content) : "") +
                     Input("FINAL DRAFT UNDER REVIEW", draft.Content) +
                     "\nTask: Conduct the final quality review against all editorial standards and the assignment, " +
                     "score it, list every issue, and decide whether to approve it or return it for revision.";

        var r = await CallAsync(p, stage, new AgentTask { Agent = AgentId.QualityManager, OutputFormat = QualityRules.OutputFormat, Prompt = prompt },
            ArtifactKind.QualityReport, ct);
        if (r == null) return false;

        var report = QualityRules.Parse(r.Text, draft.Content);
        if (report == null)
        {
            // One repair attempt: ask for the same report as valid JSON.
            Log(p, ActivityLevel.Warning, AgentId.QualityManager, "Quality report was not valid JSON; asking the agent to reformat it.");
            var repair = await _runner.RunAsync(new AgentTask
            {
                Agent = AgentId.QualityManager,
                OutputFormat = QualityRules.OutputFormat,
                WebSearchOverride = false,
                Prompt = Input("Your previous reply", r.Text) +
                         "\nYour previous reply could not be read as JSON. Return the same review as ONLY the JSON object described in the output format.",
            }, ct).ConfigureAwait(false);
            report = repair.IsComplete ? QualityRules.Parse(repair.Text, draft.Content) : null;
            if (report == null)
                throw new InvalidOperationException("The quality report could not be read after one retry.");
        }

        var qa = AddVersion(p, ArtifactKind.QualityReport, AgentCatalog.QualityManager.Name, AgentId.QualityManager,
            QualityRules.ToMarkdown(report), draft.Id, r.Response,
            note: report.Decision == QaDecision.Approve ? "Approved" : "Returned for revision",
            data: JsonSerializer.Serialize(report, Json.Options));
        stage.OutputVersionId = qa.Id;
        p.LatestQualityReportVersionId = qa.Id;

        if (report.GateOverrodeModel)
            Log(p, ActivityLevel.Warning, AgentId.QualityManager, "Approval blocked by editorial rules: " + string.Join(" ", report.GateReasons));

        if (report.Decision == QaDecision.Approve)
        {
            p.QaApproved = true;
            Message(p, AgentCatalog.QualityManager.Name, UserName, MessageType.Approval,
                $"Approved. Overall score {report.Overall:0.0}/10.", qa.Id);
            return true;
        }

        p.QaApproved = false;
        if (p.QaRevisionsInCycle < settings.MaxRevisionRounds)
        {
            p.QaRevisionsInCycle++;
            p.RevisionRound++;
            p.PendingRevisionRequests = report.RevisionRequests.ToList();
            Message(p, AgentCatalog.QualityManager.Name, AgentCatalog.Writer.Name, MessageType.RevisionRequest,
                $"Returned for revision: {report.RevisionRequests.Count} request(s), score {report.Overall:0.0}/10.", qa.Id);
            stage.Status = StageStatus.Completed;
            stage.CompletedAt = DateTime.UtcNow;
            Log(p, ActivityLevel.Info, AgentId.QualityManager, $"QA returned the draft for revision (round {p.RevisionRound}).");
            ResetStages(p, StageKind.Draft);
            return true;
        }

        Message(p, AgentCatalog.QualityManager.Name, UserName, MessageType.QualityReport,
            $"Not approved after {settings.MaxRevisionRounds} revision round(s). Needs your attention.", qa.Id);
        Log(p, ActivityLevel.Warning, AgentId.QualityManager, "Revision limit reached without QA approval.");
        return true;
    }

    private void Deliver(Project p, StageState stage)
    {
        stage.Status = StageStatus.Completed;
        stage.StartedAt ??= DateTime.UtcNow;
        stage.CompletedAt = DateTime.UtcNow;
        p.Status = ProjectStatus.AwaitingApproval;
        p.StatusDetail = p.QaApproved
            ? "Approved by the Editorial Quality Manager — waiting for your final decision."
            : "NOT approved by the Editorial Quality Manager — read the quality report before deciding.";
        Message(p, "Editorial team", UserName, MessageType.Draft, "Final content delivered for your review.", p.CurrentDraftVersionId);
        Log(p, p.QaApproved ? ActivityLevel.Info : ActivityLevel.Warning, null, "Delivered for your review.");
    }

    // ===================================================================== on-demand tools

    public async Task<ArtifactVersion> GenerateSocialPackAsync(string projectId, CancellationToken ct = default)
    {
        var p = LoadIdle(projectId);
        var draft = CurrentDraft(p);
        var agent = AgentCatalog.SocialMedia;
        EnsureContext(agent, p.AllowIncompleteContext);
        Tracker.Started(agent.Id, "Social media pack — " + p.Title, p.Id);
        try
        {
            var r = await _runner.RunAsync(new AgentTask
            {
                Agent = agent.Id,
                OutputFormat = SocialFormat,
                Prompt = BriefBlock(p) + Input("Approved article", draft.Content) + AnswersBlock(p) +
                         "\nTask: Turn this article into platform-specific social media content.",
            }, ct).ConfigureAwait(false);

            var v = AddVersion(p, ArtifactKind.SocialPack, r, draft.Id,
                note: r.IsComplete ? (p.Status == ProjectStatus.Approved ? null : "Generated before final approval") : "Incomplete — token limit",
                incomplete: !r.IsComplete);
            Message(p, agent.Name, UserName, MessageType.Draft, "Social media pack ready.", v.Id);
            Log(p, ActivityLevel.Info, agent.Id, "Social media pack created.");
            Save(p);
            Tracker.Finished(agent.Id);
            return v;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Tracker.Failed(agent.Id, ex is AiException ae ? ae.UserMessage : ex.Message);
            throw;
        }
        finally
        {
            if (Tracker.Get(agent.Id).State == AgentWorkState.Working) Tracker.Finished(agent.Id);
        }
    }

    public async Task<CalendarResult> GenerateCalendarAsync(DateTime start, bool monthly, string? focus, CancellationToken ct = default)
    {
        var agent = AgentCatalog.Strategist;
        EnsureContext(agent, false);
        var days = monthly ? DateTime.DaysInMonth(start.Year, start.Month) - start.Day + 1 : 7;
        var end = start.AddDays(days - 1);
        Tracker.Started(agent.Id, monthly ? "Monthly content calendar" : "Weekly content calendar", null);
        try
        {
            var r = await _runner.RunAsync(new AgentTask
            {
                Agent = agent.Id,
                OutputFormat = CalendarFormat,
                WebSearchOverride = false,
                Prompt = $"Task: Create a {(monthly ? "monthly" : "weekly")} content calendar from {start:yyyy-MM-dd} to {end:yyyy-MM-dd} " +
                         "following our publishing frequency, content categories and formats." +
                         (string.IsNullOrWhiteSpace(focus) ? "" : "\n" + Input("Focus requested by the user", focus)),
            }, ct).ConfigureAwait(false);

            var json = AgentOutput.TryParseJsonObject(r.Text)
                ?? throw new InvalidOperationException("The Strategist's calendar could not be read. Try again.");
            var entries = new List<CalendarEntry>();
            if (json["entries"] is JsonArray arr)
            {
                foreach (var e in arr.OfType<JsonObject>())
                {
                    if (!DateTime.TryParseExact(S(e["date"]), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) continue;
                    entries.Add(new CalendarEntry
                    {
                        Date = d, Title = S(e["title"]), Format = S(e["format"]), Category = S(e["category"]),
                        Platform = S(e["platform"]), Keyword = S(e["keyword"]), Notes = S(e["notes"]),
                    });
                }
            }
            var themes = (json["themes"] as JsonArray)?.Select(x => S(x)).Where(x => x.Length > 0).ToList() ?? new();
            var questions = (json["questions"] as JsonArray)?.Select(x => S(x)).Where(x => x.Length > 0).ToList() ?? new();

            var v = _repo.AddVersion(new ArtifactVersion
            {
                ProjectId = ToolsProjectId, Kind = ArtifactKind.Calendar, Author = agent.Name, Agent = agent.Id,
                Content = r.Text, Usage = r.Response.Usage, IsIncomplete = !r.IsComplete,
                Note = $"{(monthly ? "Monthly" : "Weekly")} calendar from {start:d MMM yyyy}",
            });
            Tracker.Finished(agent.Id);
            return new CalendarResult(entries.OrderBy(e => e.Date).ToList(), themes, questions, v);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Tracker.Failed(agent.Id, ex is AiException ae ? ae.UserMessage : ex.Message);
            throw;
        }
        finally
        {
            if (Tracker.Get(agent.Id).State == AgentWorkState.Working) Tracker.Finished(agent.Id);
        }
    }

    public async Task<ArtifactVersion> RunSeoToolAsync(string text, string? keyword, CancellationToken ct = default)
    {
        var agent = AgentCatalog.Seo;
        Tracker.Started(agent.Id, "SEO tool analysis", null);
        try
        {
            var r = await _runner.RunAsync(new AgentTask
            {
                Agent = agent.Id,
                OutputFormat = SeoFormat,
                Prompt = (string.IsNullOrWhiteSpace(keyword) ? "" : $"Target keyword: {keyword}\n") +
                         Input("Text to analyse (if this is only a topic, give keyword research and a recommended structure instead of a draft)", text) +
                         "\nTask: Conduct keyword research and an SEO review.",
            }, ct).ConfigureAwait(false);
            var v = _repo.AddVersion(new ArtifactVersion
            {
                ProjectId = ToolsProjectId, Kind = ArtifactKind.SeoToolReport, Author = agent.Name, Agent = agent.Id,
                Content = r.Text, Usage = r.Response.Usage, IsIncomplete = !r.IsComplete,
                Sources = r.Response.Citations, ConsultedSources = r.Response.SearchResults,
                Note = string.IsNullOrWhiteSpace(keyword) ? Shorten(text, 60) : "Keyword: " + keyword,
            });
            Tracker.Finished(agent.Id);
            return v;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Tracker.Failed(agent.Id, ex is AiException ae ? ae.UserMessage : ex.Message);
            throw;
        }
        finally
        {
            if (Tracker.Get(agent.Id).State == AgentWorkState.Working) Tracker.Finished(agent.Id);
        }
    }

    private void EnsureContext(AgentDefinition agent, bool allowIncomplete)
    {
        if (!_settings().StrictContext || allowIncomplete) return;
        var missing = ContextValidator.FindMissing(_context, agent.ContextFiles);
        if (missing.Count > 0) throw new ContextMissingException(missing);
    }

    // ===================================================================== helpers

    private Project Load(string id) => _repo.GetProject(id) ?? throw new InvalidOperationException("Project not found.");

    private Project LoadIdle(string id)
    {
        if (IsRunning(id)) throw new InvalidOperationException("Pause or stop the project first.");
        return Load(id);
    }

    private void Save(Project p)
    {
        _repo.SaveProject(p);
        ProjectChanged?.Invoke(this, p.Id);
    }

    private ArtifactVersion? Output(Project p, StageKind kind)
    {
        var id = p.Stage(kind).OutputVersionId;
        return id == null ? null : _repo.GetVersion(p.Id, id);
    }

    private ArtifactVersion? LatestVersion(Project p, ArtifactKind kind) =>
        _repo.ListVersions(p.Id).Where(v => v.Kind == kind).OrderBy(v => v.CreatedAt).LastOrDefault();

    private ArtifactVersion CurrentDraft(Project p) =>
        (p.CurrentDraftVersionId != null ? _repo.GetVersion(p.Id, p.CurrentDraftVersionId) : null)
        ?? throw new InvalidOperationException("There is no draft yet.");

    private static void ResetStages(Project p, StageKind from)
    {
        foreach (var s in p.Stages.Where(s => s.Kind >= from))
        {
            s.Status = StageStatus.Pending;
            s.Error = null;
            s.Questions.Clear();
            s.BlockedByContext = false;
        }
    }

    private bool AcceptRevisedDraft(Project p, AgentId agent, string before, string? after)
    {
        var name = AgentCatalog.Get(agent).Name;
        if (string.IsNullOrWhiteSpace(after))
        {
            Log(p, ActivityLevel.Warning, agent, $"{name} did not return a revised draft; the previous draft was kept.");
            return false;
        }
        if (after.Length < before.Length * 0.5)
        {
            Log(p, ActivityLevel.Warning, agent, $"{name}'s draft was less than half the original length; it was saved for reference but not used.");
            AddVersion(p, ArtifactKind.Draft, name, agent, after, null, null, note: "Rejected automatically: too short");
            return false;
        }
        var lost = CountLinks(before) - CountLinks(after);
        if (lost > 0)
            Log(p, ActivityLevel.Warning, agent, $"{name} removed {lost} source link(s). QA will check the affected claims.");
        return true;
    }

    private static readonly Regex LinkPattern = new(@"\]\(https?://", RegexOptions.Compiled);
    internal static int CountLinks(string text) => LinkPattern.Matches(text).Count;
    internal static int WordCount(string text) => Regex.Matches(text, @"\b[\p{L}\p{N}'’-]+\b").Count;

    private ArtifactVersion AddVersion(Project p, ArtifactKind kind, AgentResult r, string? parentId,
        string? note = null, bool incomplete = false, string? contentOverride = null)
    {
        var agent = AgentCatalog.Get(r.Agent);
        return AddVersion(p, kind, agent.Name, agent.Id, contentOverride ?? r.Text, parentId, r.Response, note, incomplete);
    }

    private ArtifactVersion AddVersion(Project p, ArtifactKind kind, string author, AgentId? agent, string content,
        string? parentId, AiResponse? response, string? note = null, bool incomplete = false, string? data = null)
    {
        return _repo.AddVersion(new ArtifactVersion
        {
            ProjectId = p.Id,
            Kind = kind,
            Author = author,
            Agent = agent,
            Content = content,
            ParentId = parentId,
            RevisionRound = p.RevisionRound,
            Sources = response?.Citations.ToList() ?? new(),
            ConsultedSources = response?.SearchResults.ToList() ?? new(),
            Usage = response?.Usage,
            IsIncomplete = incomplete,
            Note = note,
            Data = data,
        });
    }

    private static void Message(Project p, string from, string to, MessageType type, string summary, string? versionId = null) =>
        p.Messages.Add(new AgentMessage { From = from, To = to, Type = type, Summary = summary, VersionId = versionId });

    private static void Log(Project p, ActivityLevel level, AgentId? agent, string text) =>
        p.Activity.Add(new ActivityEntry { Level = level, Agent = agent, Text = text });

    private static string Shorten(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private static string S(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : n?.ToString() ?? "";
}
