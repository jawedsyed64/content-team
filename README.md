# Qalam — AI Content Team

A Windows desktop app that runs eight specialised AI agents as a coordinated
content agency: strategy → research → planning → writing → SEO → editing →
quality review → your approval.

* **Real agents, real work.** Each agent is a separate Claude prompt with its own
  role, context files and output format. Status on the dashboard changes only
  when a real API call starts, finishes or fails.
* **Research with sources.** The Research Analyst uses Claude's web search; the
  links it cites are captured and travel with the research brief.
* **No invented facts.** Agents may not invent statistics, quotes, sources or
  brand details. Missing information → the project stops and asks you.
* **Quality gate.** The Editorial Quality Manager scores seven criteria and
  cannot approve content with unresolved factual problems (enforced in code).
* **Every version kept.** Drafts, briefs and reports are immutable versions;
  your own edits become new versions too.
* **You decide.** Nothing is published. You approve, request changes or reject.

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the design and
[docs/SETUP.md](docs/SETUP.md) to get started.

## Dashboard sections

Overview · AI Agents · Content Projects · Research Library · Content Calendar ·
Writing Workspace · SEO Tools · Social Media Content · Editorial Review ·
Brand Guidelines · Settings

## Build

```
dotnet test tests/Qalam.Core.Tests          # workflow engine tests (any OS)
dotnet publish src/Qalam.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

GitHub Actions builds and tests every push; download **Qalam-win-x64** from the
run's *Artifacts* section.
