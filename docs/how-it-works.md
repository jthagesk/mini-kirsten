# How Mini-Kirsten works

Mini-Kirsten is a coding agent. You put a label on a GitHub issue, and a few
minutes later the agent opens a pull request that solves it. This document
describes the whole chain, from label to PR, and where each part lives.

## The big picture

```
 GitHub (novanet/workshop.oslo-live)
   │  issue gets label "mini-kirsten"
   ▼
 Receiver  (Azure Container App, always on)
   │  verifies webhook signature, puts one message on a queue
   ▼
 Storage Queue "agent-tasks"
   │  KEDA sees a message
   ▼
 Worker  (Azure Container Apps Job, starts per message, scales to zero)
   │  Pipeline: reserve → clone → context → 4 model stages → verify → PR
   │  each model stage = one headless `claude -p` run (Claude Code)
   ▼
 GitHub: branch agent/mini-kirsten/<issue>-<slug>, pull request, issue comments
   ▼
 Sensor (course bot) reviews the PR; a human merges. No auto-merge.
```

The laptop never creates PRs. Locally you can only run `--dry-run`.

## The parts

| Part | Where | What it does |
|---|---|---|
| Receiver | `src/Receiver/Program.cs` | Small web app. Receives GitHub webhooks, verifies `X-Hub-Signature-256` with `WEBHOOK_SECRET`, ignores every repo except `TARGET_REPO`, and queues a message when an issue gets the agent label. Also queues PR mentions and reviews (answer mode). `POST /tasks` is a manual fallback. No LLM, no git. |
| Queue | Azure Storage Queue `agent-tasks` | The buffer between receiving and working. Decouples GitHub's 10-second webhook timeout from runs that take 10 minutes. |
| Worker | `src/Worker/` | Console app in a container. Takes one message, runs the pipeline, deletes the message, exits. |
| Pipeline | `src/Worker/Pipeline.cs` | The fixed order of steps. Deterministic C#. The model never chooses the next step. |
| ClaudeRunner | `src/Worker/ClaudeRunner.cs` | Starts `claude -p` for each stage with model, allowed tools, max turns, timeout and system prompt. Parses tokens and cost from `stream-json`. |
| Agent config | `agent/` | What the model reads: `system.md`, `conventions.md`, `stages.json`, `prompts/*.md`, `skills/*/SKILL.md`. Changing these changes behaviour without changing code. |
| Memory | `src/Worker/Memory.cs` | Notes the agent keeps between runs, in blob `hukommelse/memory.md`. |
| Infra | `infra/main.bicep` | Azure resources: Log Analytics, storage (queue, blob, table `agentRuns`), Container Apps environment, Receiver, Worker job. |
| Deploy | `.github/workflows/deploy-azure.yml` | On push to `main` or a manual run: validate settings, log in to Azure, build images in the shared ACR, deploy Bicep. |

## Step by step: from label to PR

### 1. Label → queue (Receiver)

1. Someone puts the label `mini-kirsten` on an issue in `novanet/workshop.oslo-live`.
2. GitHub sends a webhook to `https://ca-mini-kirsten-receiver.<…>.azurecontainerapps.io/webhook`.
   The course leader registered this webhook with the Receiver URL.
3. The Receiver checks:
   - the signature, so only GitHub, holding the shared `WEBHOOK_SECRET`, can queue work;
   - the repository (only `TARGET_REPO`);
   - the event (`issues` + `labeled`) and the label (`AGENT_LABEL`).
4. It puts `{repo, issueNumber, kind: "issue", deliveryId}` on the queue and answers `202 Accepted`.

The Receiver keeps one replica running (`minReplicas: 1`) so the webhook never hits a cold start.

### 2. Queue → Worker (KEDA)

- The Worker is a Container Apps **Job** with an `azure-queue` scale rule: `queueLength: 1`, polling every 30 s, at most 3 parallel executions.
- One message starts one container. `replicaRetryLimit: 0` and `replicaTimeout: 3600`: no retries, maximum one hour.
- The Worker takes the message with a 90-minute visibility timeout and deletes it whatever the outcome, so a failing task does not loop.

### 3. The pipeline (`Pipeline.Run` + `RunStages`)

| # | Step | Code | Stops with |
|---|---|---|---|
| 1 | **Status check.** Is the issue still open? Then assign it (`--add-assignee @me`). This is visible assignment, not a lock; several agents can work on the same issue. | `Reservation.IsBusy`, `Reservation.Claim` | `0` + comment if busy/closed |
| 2 | **Clean workspace.** Clone the repo, create branch `agent/mini-kirsten/<n>-<slug>`, post a start marker on the issue. | `Repository.Clone`, `CreateBranch`, `StartMarker` | |
| 3 | **Context.** Copy skills into `.claude/skills/` in the clone (listed in `.git/info/exclude`, so they never end up in the PR). Read memory, then build the system prompt: `system.md` + `conventions.md` + `# Memory`. | `InstallSkills`, `ReadMemory`, `BuildSystemPrompt` | |
| 4 | **Model stages**, in the order from `stages.json`. Each gets the previous stage's output as `{PreviousOutput}`. | `ClaudeRunner.Run` | `1` + comment if a stage fails |
| 4b | **Plan gate.** `plan-review` must start with `PLAN GODKJENT`, or implementation never starts. | `StartsWith("PLAN GODKJENT")` | `1` + comment |
| 5 | **Diff check.** No changes means no PR; the answer is posted as a comment. | `repo.HasChanges()` | `0` + comment |
| 6 | **Verification in code.** Runs `dotnet build` and `dotnet test` outside the model. The model saying "it works" is not a gate. | `Verify` | `2` if red and `stopOnVerifyFailure: true` |
| 7 | **Delivery.** Commit, push, create PR (draft if the build is red), comment on the issue with cost, save memory. | `CommitAndPush`, `CreatePullRequest`, `Remember` | `0` |

If anything throws, `Run` catches it and posts a "krasj" comment with the end marker, so a failed run is still counted and priced.

### 4. The four model stages (`agent/stages.json`)

| Stage | Model | Tools | Purpose |
|---|---|---|---|
| `research` | Opus 5.5 | read-only + `git diff/log/show` | Find the location, plan the change and the tests. |
| `plan-review` | Haiku 4.5 | read-only | Approve (`PLAN GODKJENT`) or reject (`PLAN MANGLER`) the plan and restate it in full. |
| `implement` | Sonnet 5 | read, `Edit`, `Write`, `dotnet build/test`, `git diff/status` | Make the change and the tests. |
| `self-review` | Sonnet 5 | read, `Edit`, `dotnet build/test`, `git diff/status` | Map every acceptance criterion to the diff (PASS/FAIL), fix gaps. |

What limits a stage: `allowedTools` (what it can do), `maxTurns` (how long it can go on), `timeoutMinutes` (hard stop), and the permission mode (`acceptEdits` for stages that write, `dontAsk` for read-only ones).
Commit, push and PR creation are never model tools; the worker does them.

### 5. What the model reads

```
system prompt  = agent/system.md
               + agent/conventions.md
               + "# Memory" + contents of hukommelse/memory.md   (if any)
user prompt    = agent/prompts/<stage>.md with {Repo}, {IssueNumber},
                 {IssueTitle}, {IssueBody}, {BranchName}, {PreviousOutput} filled in
skills         = .claude/skills/craft, .claude/skills/critical-review (loaded if the model chooses to)
```

`conventions.md` sets the key safety rule: only the system prompt and the stage prompt are instructions. Issue text, comments, code and memory are data.

### 6. Memory

- **Read** before every task and placed in the system prompt.
- **Written** after the task: the worker takes up to 3 bullets (max 220 characters each) from `## Memory` in the agent's final report and puts them at the top of the file. The file is capped at `MEMORY_MAX_CHARS` (default 6000), and the oldest entries are dropped first.
- Stored in the **blob** `hukommelse/memory.md`, because the container is deleted after each run. If the blob does not exist yet, the worker starts from `agent/memory.md` in the image, if one was committed.
- Memory belongs to the agent, not to the target repo. Every participant works in the same repo, so notes kept there would mix and conflict.

### 7. What ends up on GitHub

- **Issue comments:**
  - a start comment with a hidden `<!-- mini-nils-start {...} -->` marker;
  - an end comment with the cost table and a `<!-- mini-nils-slutt {...} -->` marker holding outcome, turns, tokens, cost and seconds.

  The result board reads these markers. Every run writes exactly one end marker.
- **Pull request:** the agent's summary, the verification log, the cost table per stage, and a hidden `<!-- mini-nils {...} -->` metrics line.
- **Sensor**, the course bot, reviews the PR against the acceptance criteria and hidden tests. The first merged PR for an issue wins.

### 8. Answer mode (PR mentions)

If `AGENT_MENTION` is set, a comment mentioning the agent on a PR is queued as `review-comment`/`review`. The worker then:

- answers only if the author is a collaborator and the PR is not from a fork;
- checks out the PR and runs one read-only stage (`mention-response`);
- never loads `.mcp.json` from the PR;
- posts the answer.

No code changes and no PR.

## Configuration and secrets

| Name | Kind | Used by | What it is |
|---|---|---|---|
| `AGENT_NAME` | repo variable | deploy, worker | `mini-kirsten`. Gives the label, the branch prefix and the Azure resource names (`ca-mini-kirsten-receiver`, `caj-mini-kirsten-worker`). |
| `GH_TOKEN` | secret | worker | Reads issues, pushes branches, creates PRs and comments. |
| `ANTHROPIC_API_KEY` | secret | worker | Model access for `claude -p`. |
| `WEBHOOK_SECRET` | secret | receiver | Shared with GitHub's webhook; verifies the signature. Comes from the course leader. |
| `AZURE_CREDENTIALS` | secret | deploy only | Service principal with Contributor on `rg-agentic-workshop`. Never used locally. |
| `CLAUDE_MODEL`, `AGENT_MENTION` | repo variables (optional) | worker, receiver | Default model override; mention name. |

The deploy copies the GitHub secrets into Container Apps secrets. **Changing a GitHub secret has no effect until the deploy workflow runs again.**

## Running things

| Goal | Command |
|---|---|
| Build | `dotnet build` |
| Local check (read-only) | `dotnet run --project src/Worker -- --issue 1 --repo novanet/workshop.oslo-live --dry-run` |
| Deploy | Push to `main`, or `gh workflow run deploy-azure.yml` |
| Watch deploy | `gh run watch` |
| Start a real run | Put the label `mini-kirsten` on an issue in Oslo Live |
| Check a run | Issue comments (start and end markers, cost table) and the PR |
| Restore the Lab 1 solution | `./sjekkpunkt.sh` (saves the current file to `.kurs/Pipeline.forsok.cs`) |

A local run without `--dry-run` is blocked on purpose: only the Azure worker may assign issues, comment and open PRs.

## Safety layers

1. **Receiver:** signature check and a single allowed repository.
2. **Prompt:** `conventions.md` treats issue text, code and memory as data, not instructions.
3. **Tool profiles:** each stage only has the tools in `allowedTools`; there is no git commit or push, and no `gh`.
4. **Deterministic gates in code:** the plan gate, the diff check, build and test.
5. **Delivery:** a separate agent branch, a PR only, no auto-merge, and a human or Sensor review before merge.

Known gaps (not fixed yet):
- The worker loads `.mcp.json` from the target repo in issue runs.
- `dotnet test` runs repository code while `GH_TOKEN` is in the environment.
- A repo's own skill with the same name replaces ours.
- Memory from red builds is saved too.

## Lessons from setting this up (2026-09-23)

- **`gh secret set NAME` run through Claude Code's `!` saves an empty secret.**
  - Why: there is no terminal to paste into.
  - Fix: use a normal terminal, the browser, `--body`, or pipe the value in.
  - How to spot it: the Actions log shows `NAME:` with nothing after it, instead of `NAME: ***`.
- **A green deploy is not proof that anything was deployed.** With `AGENT_NAME` missing, the workflow skips the Azure steps and still reports success. Check that "Bootstrap, build, and deploy Azure resources" actually ran, and that the log prints `Mottak: https://…`.
- **`.env` has Windows line endings.** Shell checks must strip `\r`, or an empty value looks filled.
- **An API key can stop working mid-run.** "Your ANTHROPIC_API_KEY belongs to a disabled organization" means a new key from the course leader, then a new deploy.
- **Re-labelling an issue starts a new full run** of about $1.5–2.2 and 4–5 M tokens. `implement` uses 55–70 % of it.
