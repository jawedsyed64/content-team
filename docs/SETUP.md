# Setup guide

## 1. What you need

| Item | Why | Cost |
|---|---|---|
| Windows 10 or 11 (64-bit) | Qalam is a Windows desktop app | — |
| A Claude API key from Anthropic | Powers all eight agents | Pay per use |
| Web search enabled on your Anthropic account | Lets the Research Analyst find and cite sources | Charged per search |

You do **not** need to install .NET — the download is self-contained.

## 2. Get the app

1. Open the repository on GitHub → **Actions** tab.
2. Click the latest green **Build and test** run.
3. Under **Artifacts**, download **Qalam-win-x64** and unzip it.
4. Run **Qalam.exe**. (If Windows SmartScreen warns about an unknown publisher,
   choose *More info → Run anyway*; the app is not code-signed.)

## 3. Get a Claude API key

1. Go to <https://console.anthropic.com> and sign in or create an account.
2. Add billing credit under **Billing**.
3. Open **API keys → Create key**, copy the key (it starts with `sk-ant-`).
4. Optional but recommended: in the Console, make sure **web search** is
   enabled for your organisation in the Console settings. If it is not,
   research stages will fail with a permission message, or you can switch web
   search off in Qalam's Settings (research will then be unverified and QA will
   block unsourced claims).

## 4. First run

1. **Settings** → paste the key → **Save key** → **Test connection**.
   The key is encrypted with Windows DPAPI under your Windows account.
2. Choose the model (default `claude-sonnet-5-5`). Optionally pick a stronger
   model (e.g. `claude-opus-5-5`) for the Research Analyst and Quality Manager.
3. **Brand Guidelines** → open each of the five files and replace every
   `[FILL IN: …]` with your own details. Upload any existing brand documents
   (.txt, .md, .docx). Until a file is complete, agents that need it will stop
   and ask you rather than invent anything.
4. **Content Projects → + New content project** → enter a topic, pick a format,
   optionally word count, tone, language, platform and keyword → **Create**.

## 5. Running a project

* The **Workflow** tab shows each stage, the agent doing it and its status.
* **Pause after this stage** waits for the current agent to finish;
  **Stop now** cancels the running API call (that stage is retried on resume).
* If an agent asks questions, a yellow box appears — answer and continue, or
  continue without answering (the agent will mark unknowns instead of guessing).
* If a stage fails (network, rate limit, overloaded), fix the cause and press
  **Start / Resume**: finished stages are not repeated.
* **Outputs & versions** shows every brief, draft and report. **Agent messages**
  shows each hand-off. **Task history** is the full log.

## 6. Reviewing and approving

Open **Editorial Review**: read the final draft, the quality scores, the issues
and the full report. Then **Approve**, **Request changes** (your feedback goes to
the Writer and the draft passes SEO, editing and QA again) or **Reject**.

You can also edit the draft yourself in **Writing Workspace** — it is saved as a
new version — and send it for a **QA re-check**.

> An automated quality score is not proof of factual accuracy or originality.
> Open the source links and check important claims before publishing.

## 7. Other tools

* **Content Calendar** — the Content Strategist plans a week or the rest of a
  month from your strategy file; turn any entry into a project.
* **SEO Tools** — keyword ideas and an SEO review for any topic or text.
* **Social Media Content** — LinkedIn, Facebook, Instagram, X posts and reel
  ideas from a project's draft.
* **Research Library** — every research brief and its sources, searchable.

## 8. What is not included

* Keyword search volumes and difficulty scores (need a paid SEO data service).
* Plagiarism scanning (needs a service such as Copyscape).
* Automatic publishing to websites or social networks.

## 9. Where your data lives

`%APPDATA%\Qalam` — projects, versions, context files (with history), brand
documents, calendar, settings and the encrypted key. Back up this folder to keep
your work. Set the `QALAM_DATA` environment variable to use another folder.

## 10. Costs

Each project makes 7–15 Claude API calls (more with revision rounds), plus
web searches during research, SEO and QA. Lower **Max web searches per stage**
or use a smaller model in Settings to reduce cost. Check usage in the Anthropic
Console.
