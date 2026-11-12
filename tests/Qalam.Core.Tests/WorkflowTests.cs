using Qalam.Core.AI;
using Qalam.Core.Models;
using Qalam.Core.Workflow;
using Xunit;

namespace Qalam.Core.Tests;

public class WorkflowTests
{
    [Fact]
    public async Task Full_pipeline_runs_every_agent_in_order_and_waits_for_user_approval()
    {
        using var h = new Harness();
        var p = h.NewProject();

        await h.Workflow.RunAsync(p.Id);
        p = h.Reload(p);

        Assert.Equal(ProjectStatus.AwaitingApproval, p.Status);
        Assert.True(p.QaApproved);
        Assert.All(p.Stages, s => Assert.Equal(StageStatus.Completed, s.Status));
        Assert.Equal(new[]
        {
            AgentId.Strategist, AgentId.ResearchAnalyst, AgentId.Ideation, AgentId.Writer,
            AgentId.Seo, AgentId.CopyEditor, AgentId.QualityManager,
        }, h.Ai.Requests.Select(r => r.Agent!.Value));

        // Research uses web search; the Writer does not.
        Assert.True(h.Ai.Requests.Single(r => r.Agent == AgentId.ResearchAnalyst).UseWebSearch);
        Assert.False(h.Ai.Requests.Single(r => r.Agent == AgentId.Writer).UseWebSearch);

        // Agents receive their context files, and each agent's output is handed to the next.
        var writer = h.Ai.Requests.Single(r => r.Agent == AgentId.Writer);
        Assert.Contains("CONTEXT-BrandVoice", writer.System);
        Assert.Contains("CONTEXT-StyleGuide", writer.System);
        Assert.DoesNotContain("CONTEXT-EditorialStandards", writer.System);
        Assert.Contains("42% of firms use X", writer.UserMessage);       // research brief
        Assert.Contains("Why X matters", writer.UserMessage);            // content plan
        Assert.Contains(Scripts.Url1, writer.UserMessage);               // verified source list
        Assert.Contains("CONTEXT-EditorialStandards", h.Ai.Requests.Single(r => r.Agent == AgentId.QualityManager).System);

        var versions = h.Repo.ListVersions(p.Id);
        Assert.Equal(3, versions.Count(v => v.Kind == ArtifactKind.Draft)); // writer, SEO, copy editor — none overwritten
        Assert.Single(versions, v => v.Kind == ArtifactKind.QualityReport);
        var research = versions.Single(v => v.Kind == ArtifactKind.ResearchBrief);
        Assert.Contains(research.Sources, s => s.Url == Scripts.Url1);
        Assert.Contains(research.ConsultedSources, s => s.Url == Scripts.Url2);

        var current = h.Repo.GetVersion(p.Id, p.CurrentDraftVersionId!)!;
        Assert.Equal(AgentId.CopyEditor, current.Agent);
        Assert.Contains("Edited.", current.Content);

        // Collaboration record
        Assert.Contains(p.Messages, m => m.Type == MessageType.ResearchBrief && m.To == "Content Writer");
        Assert.Contains(p.Messages, m => m.Type == MessageType.EditedDraft && m.To == "Editorial Quality Manager");
        Assert.Contains(p.Messages, m => m.Type == MessageType.Approval);
    }

    [Fact]
    public async Task Qa_cannot_approve_content_with_unresolved_factual_issue()
    {
        using var h = new Harness();
        h.Ai.Handler = (r, n, ct) => Task.FromResult(r.Agent == AgentId.QualityManager && n == 1
            ? Scripts.Text(Scripts.QaJson("approve", Scripts.FactualIssue()))   // model says approve, but…
            : Scripts.Default(r, n));
        var p = h.NewProject();

        await h.Workflow.RunAsync(p.Id);
        p = h.Reload(p);

        Assert.Equal(2, h.Ai.CallsTo(AgentId.Writer));
        Assert.Equal(2, h.Ai.CallsTo(AgentId.QualityManager));
        Assert.Equal(1, p.RevisionRound);
        Assert.True(p.QaApproved);
        Assert.Equal(ProjectStatus.AwaitingApproval, p.Status);

        var revisionPrompt = h.Ai.Requests.Where(r => r.Agent == AgentId.Writer).Last().UserMessage;
        Assert.Contains("Revision requests", revisionPrompt);
        Assert.Contains("Fix the unsupported claim", revisionPrompt);
        Assert.Contains("Previous draft", revisionPrompt);

        Assert.Contains(p.Messages, m => m.Type == MessageType.RevisionRequest && m.To == "Content Writer");
        Assert.Contains(p.Activity, a => a.Text.Contains("Approval blocked by editorial rules"));

        var reports = h.Repo.ListVersions(p.Id).Where(v => v.Kind == ArtifactKind.QualityReport).ToList();
        Assert.Equal(2, reports.Count);
        Assert.Contains("RETURNED FOR REVISION", reports[0].Content);
        Assert.Contains("APPROVED", reports[1].Content);
        Assert.Equal(6, h.Repo.ListVersions(p.Id).Count(v => v.Kind == ArtifactKind.Draft));
    }

    [Fact]
    public async Task Revision_limit_delivers_content_clearly_marked_not_approved()
    {
        using var h = new Harness();
        h.Settings.MaxRevisionRounds = 1;
        h.Ai.Handler = (r, n, ct) => Task.FromResult(r.Agent == AgentId.QualityManager
            ? Scripts.Text(Scripts.QaJson("revise", Scripts.FactualIssue()))
            : Scripts.Default(r, n));
        var p = h.NewProject();

        await h.Workflow.RunAsync(p.Id);
        p = h.Reload(p);

        Assert.Equal(2, h.Ai.CallsTo(AgentId.Writer));
        Assert.Equal(ProjectStatus.AwaitingApproval, p.Status);
        Assert.False(p.QaApproved);
        Assert.Contains("NOT approved", p.StatusDetail);
    }

    [Fact]
    public async Task Placeholders_in_context_files_stop_the_workflow_before_any_api_call()
    {
        using var h = new Harness(fillContext: false);
        var p = h.NewProject();

        await h.Workflow.RunAsync(p.Id);
        p = h.Reload(p);

        Assert.Empty(h.Ai.Requests);
        Assert.Equal(ProjectStatus.NeedsInput, p.Status);
        var stage = p.Stage(StageKind.Strategy);
        Assert.Equal(StageStatus.NeedsInput, stage.Status);
        Assert.True(stage.BlockedByContext);
        Assert.Contains(stage.Questions, q => q.Contains("Brand or organisation name"));

        // The user answers in the project instead → this project proceeds with the answer.
        h.Workflow.AnswerQuestions(p.Id, "Brand is Acme Tools; audience is UK plumbers.");
        await h.Workflow.RunAsync(p.Id);
        p = h.Reload(p);

        Assert.Equal(ProjectStatus.AwaitingApproval, p.Status);
        Assert.All(h.Ai.Requests, r => Assert.Contains("Acme Tools", r.UserMessage));
    }

    [Fact]
    public async Task Agent_questions_pause_the_project_and_answers_reach_later_agents()
    {
        using var h = new Harness();
        h.Ai.Handler = (r, n, ct) => Task.FromResult(r.Agent == AgentId.Strategist && n == 1
            ? Scripts.Text("## Content objective\nDraft objective.\n\n**QUESTIONS FOR USER:**\n- Which region should we focus on?\n- Is there a product to mention?")
            : Scripts.Default(r, n));
        var p = h.NewProject();

        await h.Workflow.RunAsync(p.Id);
        p = h.Reload(p);
        Assert.Equal(ProjectStatus.NeedsInput, p.Status);
        Assert.Equal(2, p.Stage(StageKind.Strategy).Questions.Count);
        Assert.Equal(1, h.Ai.Requests.Count);

        h.Workflow.AnswerQuestions(p.Id, "Pakistan; no product.");
        await h.Workflow.RunAsync(p.Id);
        p = h.Reload(p);

        Assert.Equal(ProjectStatus.AwaitingApproval, p.Status);
        Assert.Contains("Pakistan; no product.", h.Ai.Requests.Single(r => r.Agent == AgentId.Writer).UserMessage);
        Assert.Contains(p.Messages, m => m.Type == MessageType.Question);
        Assert.Contains(p.Messages, m => m.Type == MessageType.UserAnswer);
    }

    [Fact]
    public async Task Api_failure_stops_the_project_and_resume_retries_the_failed_stage()
    {
        using var h = new Harness();
        h.Ai.Handler = (r, n, ct) => r.Agent == AgentId.ResearchAnalyst && n == 1
            ? throw new AiException(AiErrorKind.Overloaded, "overloaded", 529)
            : Task.FromResult(Scripts.Default(r, n));
        var p = h.NewProject();

        await h.Workflow.RunAsync(p.Id);
        p = h.Reload(p);
        Assert.Equal(ProjectStatus.Failed, p.Status);
        Assert.Equal(StageStatus.Failed, p.Stage(StageKind.Research).Status);
        Assert.Equal(StageStatus.Completed, p.Stage(StageKind.Strategy).Status);
        Assert.Contains("overloaded", p.Stage(StageKind.Research).Error, StringComparison.OrdinalIgnoreCase);

        await h.Workflow.RunAsync(p.Id);
        p = h.Reload(p);
        Assert.Equal(ProjectStatus.AwaitingApproval, p.Status);
        Assert.Equal(2, p.Stage(StageKind.Research).Attempts);
        Assert.Equal(1, h.Ai.CallsTo(AgentId.Strategist)); // completed work is not redone
    }

    [Fact]
    public async Task Truncated_response_is_kept_but_marked_incomplete()
    {
        using var h = new Harness();
        h.Ai.Handler = (r, n, ct) => Task.FromResult(r.Agent == AgentId.Writer && n == 1
            ? Scripts.Text("# Half a draft", "max_tokens")
            : Scripts.Default(r, n));
        var p = h.NewProject();

        await h.Workflow.RunAsync(p.Id);
        p = h.Reload(p);

        Assert.Equal(StageStatus.Incomplete, p.Stage(StageKind.Draft).Status);
        Assert.Equal(ProjectStatus.Failed, p.Status);
        Assert.Contains(h.Repo.ListVersions(p.Id), v => v.IsIncomplete && v.Content == "# Half a draft");

        await h.Workflow.RunAsync(p.Id);
        Assert.Equal(ProjectStatus.AwaitingApproval, h.Reload(p).Status);
    }

    [Fact]
    public async Task User_keeps_final_control_request_changes_then_approve()
    {
        using var h = new Harness();
        var p = h.NewProject();
        await h.Workflow.RunAsync(p.Id);

        h.Workflow.RequestChanges(p.Id, "Make the introduction shorter and friendlier.");
        p = h.Reload(p);
        Assert.Equal(StageStatus.Pending, p.Stage(StageKind.Draft).Status);
        Assert.Equal(StageStatus.Completed, p.Stage(StageKind.Research).Status);

        await h.Workflow.RunAsync(p.Id);
        p = h.Reload(p);
        Assert.Equal(ProjectStatus.AwaitingApproval, p.Status);
        Assert.Contains("introduction shorter", h.Ai.Requests.Where(r => r.Agent == AgentId.Writer).Last().UserMessage);
        Assert.Equal(1, h.Ai.CallsTo(AgentId.ResearchAnalyst));

        h.Workflow.ApproveByUser(p.Id, "Ready to publish");
        p = h.Reload(p);
        Assert.Equal(ProjectStatus.Approved, p.Status);
        Assert.Contains(p.Messages, m => m.Type == MessageType.UserDecision);
    }

    [Fact]
    public async Task Manual_edit_creates_a_new_version_and_invalidates_qa_approval()
    {
        using var h = new Harness();
        var p = h.NewProject();
        await h.Workflow.RunAsync(p.Id);
        var before = h.Reload(p).CurrentDraftVersionId!;
        var draftsBefore = h.Repo.ListVersions(p.Id).Count(v => v.Kind == ArtifactKind.Draft);

        var v = h.Workflow.SaveUserEdit(p.Id, "# My own version");
        p = h.Reload(p);

        Assert.Equal(v.Id, p.CurrentDraftVersionId);
        Assert.Equal(before, v.ParentId);
        Assert.Equal("You", v.Author);
        Assert.False(p.QaApproved);
        Assert.Equal(draftsBefore + 1, h.Repo.ListVersions(p.Id).Count(x => x.Kind == ArtifactKind.Draft));
        Assert.NotNull(h.Repo.GetVersion(p.Id, before)); // earlier version preserved

        h.Workflow.RequestQualityRecheck(p.Id);
        await h.Workflow.RunAsync(p.Id);
        p = h.Reload(p);
        Assert.True(p.QaApproved);
        Assert.Contains("# My own version", h.Ai.Requests.Last(r => r.Agent == AgentId.QualityManager).UserMessage);
    }

    [Fact]
    public async Task Pause_takes_effect_after_the_current_stage()
    {
        using var h = new Harness();
        var p = h.NewProject();
        h.Ai.Handler = (r, n, ct) =>
        {
            if (r.Agent == AgentId.Strategist) h.Workflow.RequestPause(p.Id);
            return Task.FromResult(Scripts.Default(r, n));
        };

        await h.Workflow.RunAsync(p.Id);
        var paused = h.Reload(p);
        Assert.Equal(ProjectStatus.Paused, paused.Status);
        Assert.Equal(StageStatus.Completed, paused.Stage(StageKind.Strategy).Status);
        Assert.Equal(StageStatus.Pending, paused.Stage(StageKind.Research).Status);

        h.Ai.Handler = (r, n, ct) => Task.FromResult(Scripts.Default(r, n));
        await h.Workflow.RunAsync(p.Id);
        Assert.Equal(ProjectStatus.AwaitingApproval, h.Reload(p).Status);
    }

    [Fact]
    public async Task Stop_cancels_the_in_flight_call_and_the_stage_is_retried_on_resume()
    {
        using var h = new Harness();
        var p = h.NewProject();
        var started = new TaskCompletionSource();
        h.Ai.Handler = async (r, n, ct) =>
        {
            if (r.Agent == AgentId.ResearchAnalyst && n == 1)
            {
                started.SetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }
            return Scripts.Default(r, n);
        };

        var run = h.Workflow.RunAsync(p.Id);
        await started.Task;
        Assert.True(h.Workflow.IsRunning(p.Id));
        h.Workflow.Stop(p.Id);
        await run;

        var stopped = h.Reload(p);
        Assert.Equal(ProjectStatus.Paused, stopped.Status);
        Assert.Equal(StageStatus.Pending, stopped.Stage(StageKind.Research).Status);
        Assert.False(h.Workflow.IsRunning(p.Id));

        await h.Workflow.RunAsync(p.Id);
        Assert.Equal(ProjectStatus.AwaitingApproval, h.Reload(p).Status);
    }

    [Fact]
    public async Task Social_pack_and_calendar_use_the_right_agents()
    {
        using var h = new Harness();
        var p = h.NewProject();
        await h.Workflow.RunAsync(p.Id);

        var pack = await h.Workflow.GenerateSocialPackAsync(p.Id);
        Assert.Equal(ArtifactKind.SocialPack, pack.Kind);
        Assert.Equal(AgentId.SocialMedia, h.Ai.Requests.Last().Agent);

        h.Ai.Handler = (r, n, ct) => Task.FromResult(Scripts.Text(
            "{\"themes\":[\"Basics\"],\"entries\":[{\"date\":\"2026-12-01\",\"title\":\"Intro to X\",\"format\":\"Blog post\",\"category\":\"How-to\",\"platform\":\"Blog\",\"keyword\":\"x basics\",\"notes\":\"\"},{\"date\":\"bad\",\"title\":\"skip\"}],\"questions\":[]}"));
        var cal = await h.Workflow.GenerateCalendarAsync(new DateTime(2026, 12, 1), monthly: false, focus: null);
        Assert.Single(cal.Entries);
        Assert.Equal("Intro to X", cal.Entries[0].Title);
        Assert.Equal(AgentId.Strategist, h.Ai.Requests.Last().Agent);
    }

    [Fact]
    public async Task Calendar_refuses_to_run_with_unfilled_context_in_strict_mode()
    {
        using var h = new Harness(fillContext: false);
        await Assert.ThrowsAsync<ContextMissingException>(() =>
            h.Workflow.GenerateCalendarAsync(DateTime.Today, true, null));
        Assert.Empty(h.Ai.Requests);
    }

    [Fact]
    public async Task Running_the_same_project_twice_at_once_is_rejected()
    {
        using var h = new Harness();
        var p = h.NewProject();
        var gate = new TaskCompletionSource<AiResponse>();
        h.Ai.Handler = (r, n, ct) => gate.Task;

        var run = h.Workflow.RunAsync(p.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Workflow.RunAsync(p.Id));
        Assert.Throws<InvalidOperationException>(() => h.Workflow.SaveUserEdit(p.Id, "x"));
        h.Workflow.Stop(p.Id);
        gate.SetCanceled();
        await run;
    }
}
