# Mini-Nils

Et byggesett for en kodeagent. Du får ferdige deler for GitHub, git, Claude Code, bygg, test, rapportering og Azure. I Lab 1 implementerer du rekkefølgen i `Pipeline.cs` etter en oppskrift, kjører en lokal dry-run og deployer til Azure fra GitHub Actions. I Lab 2 merker du en issue; da tildeler Azure-workeren issuen, skriver kommentarer og åpner PR. Laptopen din lager aldri PR.

```
GitHub-issue i Oslo Live (etikett "kari")
  → Mottak    src/Receiver   Container App     sjekker signatur, legger melding i kø
  → Kø        Storage Queue                    KEDA starter jobben 0 → 1
  → Worker    src/Worker     Container Apps Job klon, branch, claude -p, build, test, push
  → PR        i repoet, med rapport og forbrukstabell, til review
```

Pipelinen er fire modellsteg pluss levering. Modellene står i `agent/stages.json`:

| Steg | Modell |
|---|---|
| `research` | `claude-opus-5-5` |
| `plan-review` | `claude-haiku-4-5` |
| `implement` | `claude-sonnet-5` |
| `self-review` | `claude-sonnet-5` |

Implement starter bare når plan-review svarer med `PLAN GODKJENT`. Levering
(bygg, test, commit, push og PR) er kode i workeren, ikke modell. Agenten
merger aldri selv; et menneske reviewer og merger PR-en.

I Lab 3 kan monitoreringssporet bruke Azure Table Storage-tabellen
`agentRuns`, som Bicep oppretter i samme Storage Account. Tabellen er ment for
sanert kjøringsstatus; nettleseren skal lese et trygt API-endepunkt og aldri få
connection string eller andre hemmeligheter.

## Mappen

| | |
|---|---|
| `src/Worker/` | Delene til workeren. `Pipeline.cs` er orkestreringen, `ClaudeRunner.cs` er motoren, resten er git/gh/kø. |
| `setup-lab1.py` | Tilbakestiller Lab 1-skjelettet, eller gjenoppretter løsningen med `--checkpoint`, og tar vare på det forrige forsøket. |
| `sjekkpunkt.ps1` | Redningsnett i Lab 1: `./sjekkpunkt.ps1 lab1` legger inn den ferdige pipelinen. |
| `src/Receiver/` | Mottaket. Én fil. Ingen LLM, ingen git. |
| `agent/` | Alt som styrer agentens oppførsel: `system.md`, `conventions.md`, `prompts/`, `skills/`, `stages.json`. Dette er filene du endrer på kurset. Agentens hukommelse ligger i Azure som blobben `hukommelse/memory.md`. |
| `infra/main.bicep` | Hele Azure-oppsettet. |
| `.github/workflows/deploy-azure.yml` | GitHub Actions-workflow som logger inn med `AZURE_CREDENTIALS`, bygger i felles ACR og oppdaterer dine Azure-ressurser ved push til `main` eller manuell start fra Actions. |
| `docs/lab1.md` … `lab3.md` | Labbene. |
| `.devcontainer/` | Fallback hvis du ikke får installert verktøyene lokalt. Har .NET 10, gh og pwsh. |
| `.kurs/checkpoints/` | Legges inn i zippen. Inneholder en ferdig Lab 1-pipeline som redningsnett. |
| `.kurs/scaffolds/` | Legges inn i zippen. Inneholder kompilérbare Lab 1-skjeletter som scriptet kan gjenopprette. |

## Kom i gang

Du trenger PowerShell 7 (`pwsh`), .NET 10 SDK, Git, `gh` og Python 3. Du
trenger ikke `az`, Docker, Node.js eller Claude Code lokalt. Alle kommandoene
er skrevet for `pwsh`.

Du har fått dette som `mini-nils.zip`. Gjør det til ditt eget repo. Det er her agenten din bor:

```powershell
cd mini-nils
git init -b main
git add -A
git commit -m "Start fra delene til Mini-Nils"
gh repo create mini-kari --private --source . --push   # bytt kari med navnet ditt
```

Pushen starter deploy-workflowen, men den hopper over med en notis så lenge
`AGENT_NAME` ikke er satt.

Så:

1. Kjør `Copy-Item .env.example .env` og fyll inn `AGENT_NAME` og `GH_TOKEN`.
   `AGENT_NAME` er 3–12 tegn, små bokstaver, tall og bindestrek, og starter
   med en bokstav, for eksempel `kari`. Det er også etiketten agenten lytter
   etter. `GH_TOKEN` er et klassisk token med bare `public_repo` og kort
   utløp. Det når alle offentlige repoer du kan skrive til, derfor er workeren
   låst til `TARGET_REPO` (`novanet/workshop.oslo-live`). Du trenger ingen
   Anthropic-nøkkel lokalt.
2. Følg `docs/lab1.md`.

Deploy kjører bare fra GitHub Actions. Mot slutten av Lab 1 gir kurslederen
deg `AZURE_CREDENTIALS`, `ANTHROPIC_API_KEY` og `WEBHOOK_SECRET`. Du legger
dem inn som repository secrets sammen med ditt eget `GH_TOKEN`, og setter
`AGENT_NAME` som repository variable. `AZURE_CREDENTIALS` gir Contributor på
den delte ressursgruppen `rg-agentic-workshop`; bruk den bare i GitHub
Actions, aldri lokalt, og ikke del den videre. `ANTHROPIC_API_KEY` er én
felles kursnøkkel med ett felles kostnadstak. Så åpner du **Actions → Build
and deploy Mini-Nils → Run workflow**. Workflowen oppretter infrastrukturen,
bygger bildene og ruller dem ut.

Du kan kontrollere GitHub-oppsettet uten API-kall:

```powershell
dotnet run --project src/Worker -- --issue 1 --repo novanet/workshop.oslo-live --dry-run
```

`--dry-run` leser issue-status, men reserverer ikke issue, skriver kommentarer,
kloner ikke repoet og kjører ikke Claude Code. Direkte issue-kjøring uten
`--dry-run` er blokkert i starter-kitet. Etter deploy leverer webhooken
oppgaven til Azure-køen; da reserverer workeren issue, skriver kommentarer og
oppretter PR. Flere agenter kan arbeide parallelt; første mergete PR vinner.

Agenten din bor i ditt eget repo, men **jobber i [novanet/workshop.oslo-live](https://github.com/novanet/workshop.oslo-live)**, kartet alle agentene på kurset leverer pull requests til. Du trenger skrivetilgang dit fra kurslederen.

## Status

Alle agentene jobber på den samme backloggen, og Status står på storskjermen hele dagen: <https://novanet.github.io/workshop.oslo-live/>.

Hver issue har en etikett som sier hvor mye den er verdt: **1, 2, 3, 5 eller 8 poeng**, samme skala som story points. Til sammen 179.

| | |
|---:|---|
| **+1 til +8** | Issuen lukket og merget. Feilene og kjernelagene (1 til 19) må i tillegg bestå en skjult test. |
| **−1** | Hver pull request som ikke bygger |
| **−1** | Hver påbegynte 10 dollar agenten bruker |
| **−2** | PR-en som gjorde `main` rød |
| **+2** | PR-en som gjorde `main` grønn igjen |

Agenten sjekker om noen andre har issuen før den starter (`Reservation.cs`).

## Slik henger det sammen med Nils Georg

| Mini-Nils | Nils Georg (Cato.Agents) |
|---|---|
| `Receiver` (Container App) | `Cato.Agents.Api` (App Service) |
| Storage Queue + KEDA | PostgreSQL + ARM-trigger |
| `Worker` (Container Apps Job) | `Cato.Agents.Worker` i batch-modus |
| `ClaudeRunner` → `claude -p` | `CopilotSessionRunner` med C#-verktøy |
| `stages.json` | `DevOps:StageModelOverrides` + verktøyprofiler per stage |
| `agent/prompts/*.md` | `Pipeline/Prompts/0*-*.md` |
| `agent/conventions.md` | `Prompts/conventions.md` |
| `agent/skills/` | `.claude/skills/` |
| blob `hukommelse/memory.md` i Azure | `.github/agents/<navn>/memory.md` |
| `LLM request completed`-linjen | `LlmUsageLog` |
| GitHub Issues | Azure DevOps work items |

Det som mangler i Mini-Nils, og som er mesteparten av jobben i produksjon: review-fanout med flere modeller, audience-regler per mottaker, kolonne-gate på tavlen, idempotens på webhooks, chat-modus, exception- og build-triage, og all integrasjonen mot Azure DevOps.

## Sjekkpunkt

Zip-filer inneholder ikke git-historikk eller tags. Derfor følger både skjelettet
og den ferdige Lab 1-pipelinen med lokalt. Du skal skrive løsningen selv; den
ferdige pipelinen er bare et redningsnett.

Faller du av i Lab 1:

```powershell
./sjekkpunkt.ps1 lab1
dotnet build
```

Virker ikke skriptet, kjør `python setup-lab1.py --checkpoint`. Forsøket ditt
tas vare på i `.kurs/Pipeline.forsok.cs` før den komplette pipelinen legges
inn. Lab 2 bygger videre på denne.
