# Qalam — AI Content Team: System Architecture

Qalam is a Windows desktop application (C# / .NET 8 / WPF) that runs a team of
eight specialised AI agents as a coordinated content agency. Each agent is a
separate role with its own system prompt, its own context-file requirements and
its own structured output. Agents are invoked one at a time by a workflow
orchestrator through the Claude API, and every hand-off between them is stored.

> True independent multi-agent execution (agents running autonomously in
> parallel and messaging each other) is not used. As the spec allows, Qalam
> implements a **coordinated workflow**: each stage invokes one specialised
> agent role through a separate prompt, and its output becomes a recorded
> message to the next agent. Nothing is simulated: an agent's status only
> changes when a real API call starts, finishes or fails.

## 1. Solution layout

```
Qalam.sln
├── src/Qalam.Core          (net8.0 class library — no UI, cross-platform, fully testable)
│   ├── Models/             Project, ContentBrief, ArtifactVersion, AgentMessage, QualityReport, …
│   ├── Agents/             AgentDefinition, AgentCatalog (the 8 agents), AgentRunner
│   ├── AI/                 IAiClient, ClaudeClient (Messages API, web search, retries)
│   ├── Context/            ContextFile kinds, templates, ContextStore, ContextValidator
│   ├── Workflow/           WorkflowOrchestrator, stages, pause/resume, revision loop
│   ├── Quality/            QualityReport parsing, scoring rules, approval gate
│   ├── Documents/          Brand document import (.txt, .md, .docx)
│   └── Storage/            JSON file repository with version history
├── src/Qalam.App           (net8.0-windows WPF — dashboard UI, MVVM)
│   ├── Services/           SecretStore (DPAPI-encrypted API key), AppServices
│   ├── ViewModels/         One view-model per dashboard section
│   └── Views/              XAML for the 11 dashboard sections
└── tests/Qalam.Core.Tests  (xUnit — full pipeline tested with a scripted fake AI client)
```

The Core library has no Windows dependency, so the entire workflow is tested on
Linux in GitHub Actions; the WPF app is built on a Windows runner.

## 2. The agents

| # | Agent | Stage | Reads context files | Web search | Output |
|---|-------|-------|--------------------|-----------|--------|
| 1 | Content Strategist | 1 Topic selection; calendars | Brand, Audience, Strategy | optional | Content objective (JSON) / calendar (JSON) |
| 2 | Research Analyst | 2 Research | Audience, Standards | **yes** | Research brief + source list (citations captured from the API) |
| 3 | Content Ideation Specialist | 3 Planning | Brand, Audience, Strategy | no | Headlines, angles, hooks, outline |
| 4 | Content Writer | 4 Draft; revisions | Brand, Style, Audience | no | Draft (Markdown) |
| 6 | SEO Specialist | 5 SEO | Strategy, Style | optional | Recommendations + optimised draft + meta |
| 5 | Copy Editor & Proofreader | 6 Editing | Brand, Style | no | Edited draft + change log |
| 8 | Editorial Quality Manager | 7 QA | all five | optional | Quality report (JSON): scores, issues, decision |
| 7 | Social Media Manager | on demand, after approval | Brand, Audience, Strategy | no | Platform posts (LinkedIn, FB/IG, X, reels) |

Every agent prompt includes the shared rules: consult the context files, never
invent facts, statistics, quotes or sources, and list any missing information
under a `QUESTIONS FOR USER` heading instead of guessing. If an agent asks
questions, the project pauses in **Needs your input** until the user answers.

## 3. Context files (central knowledge base)

Five Markdown files, stored per workspace and editable in **Brand Guidelines**:

1. Brand Voice and Identity
2. Editorial Style Guide
3. Audience Profile
4. Content Strategy
5. Editorial Standards

Each starts from a template with `[FILL IN: …]` placeholders. The
`ContextValidator` checks, before a stage runs, whether the files that agent
needs still contain unfilled placeholders. If they do, the project stops and
asks the user — agents are never allowed to invent brand or audience details.

Uploaded brand documents (.txt, .md, .docx) are stored alongside and attached
as reference material to the agents that read brand context.

## 4. Workflow

```
User brief ─► 1 Strategist ─► 2 Research ─► 3 Ideation ─► 4 Writer ─► 5 SEO ─► 6 Copy Editor ─► 7 QA
                                                            ▲                                   │
                                                            └──── revision request (max N) ◄────┤ Revise
                                                                                                │ Approve
                                                                                                ▼
                                                                                 8 Delivery: awaiting YOUR approval
```

* **Sequential, persisted.** Each stage's output is saved before the next one
  starts. Closing the app and reopening resumes from the last completed stage.
* **Start / pause / resume / cancel.** Pause stops after the current stage;
  cancel aborts the in-flight API call.
* **QA gate (enforced in code, not just in the prompt).** If the quality
  report contains any unresolved factual issue, the decision is forced to
  *Revise* even if the model said *Approve*.
* **Revision loop.** QA revision requests go back to the Writer as a recorded
  message; the draft then passes SEO, editing and QA again. After the maximum
  number of rounds (Settings, default 2) the project is delivered to you
  clearly marked *Not approved by QA*.
* **Human control.** Nothing is "published". The final stage waits for you to
  approve, request changes (your feedback goes to the Writer) or reject.

## 5. Collaboration record and versioning

* `AgentMessage` — every hand-off (brief → writer, draft → editor,
  recommendations, revision requests, user feedback) is stored with sender,
  recipient, type and timestamp.
* `ArtifactVersion` — every draft, brief and report is a new immutable version
  with a parent link. Agents and users never overwrite: a manual edit in the
  Writing Workspace is saved as a new version authored by "You".
* `ActivityEntry` — the task history: stage started/finished/failed, API
  errors, user actions.

## 6. Quality scoring

Seven criteria, each 1–10 with a written explanation: accuracy and reliability,
relevance, readability and clarity, grammar and language, brand voice
consistency, SEO optimisation, originality and usefulness. Any score below 7
must carry a specific improvement recommendation. The UI always shows the note:
*An automated score is not proof of factual accuracy or originality.*

## 7. External services — what needs what

| Capability | Requires | Notes |
|---|---|---|
| All agent work | **Claude API key** (Anthropic) | Paid per use. Key is encrypted with Windows DPAPI for your user account. |
| Live research with source links | Claude API **web search tool** | Must be enabled for your Anthropic organisation (Console → settings). Charged per search. If disabled, the Research Analyst says it cannot verify and QA blocks approval of unverified claims. |
| Keyword search volumes, difficulty | **Not included** | Needs a paid SEO data service (e.g. Google Keyword Planner, Semrush, Ahrefs). The SEO Specialist gives keyword suggestions based on search intent and live results, and labels them as estimates. |
| Plagiarism detection | **Not included** | Needs a service such as Copyscape. QA performs an originality review, but this is not a plagiarism scan. |
| Publishing to WordPress / social networks | **Not included** | Output is copied or exported; you publish. |

## 8. Error handling

* HTTP 429 / 500 / 529 / network failures: retried with exponential back-off
  (3 attempts), then the stage is marked **Failed** with the error, and can be
  retried from the dashboard.
* `stop_reason = max_tokens`: the stage is marked **Incomplete**; the response
  is kept and the user can retry with a higher token limit.
* `stop_reason = pause_turn` (long web-search turns): the client continues the
  turn automatically.
* Malformed JSON from an agent: one automatic repair attempt, then Failed.
* 401 (bad key): workflow stops and points to Settings.

## 9. Storage

`%APPDATA%\Qalam\` (configurable):

```
settings.json            model, limits, options (no secrets)
secret.bin               API key, DPAPI-encrypted
context\*.md             the five context files
brand-docs\*             uploaded documents
projects\<id>\project.json   project, stages, messages, activity
projects\<id>\versions\*.json  immutable artifact versions
calendar.json            content calendar entries
```
