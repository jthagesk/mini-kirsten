# Lab 1: Bygg pipelinen

**Mål:** Du kobler sammen delene fra GitHub-issue til pull request i
`Pipeline.cs`. Du får ferdige komponenter, men skriver selv rekkefølgen,
kontrollene og leveringen. Du tester lokalt uten GitHub-skriving, og før
lunsj setter du secrets og kjører første deploy fra GitHub Actions. Den
første virkelige tildelingen, kommentaren og PR-en kommer fra Azure-workeren
i Lab 2, etter at kurslederen har koblet til webhooken.

Alle kommandoer er skrevet for PowerShell 7 (`pwsh`).

Du skal ikke skrive GitHub-klient, prosesshåndtering eller Claude-integrasjon
fra bunnen. De delene følger med som hjelpeklasser. Oppgaven er å bruke dem
etter oppskriften under, uten å kopiere en ferdig C#-løsning.

Når du er ferdig, skal du kunne forklare hvorfor disse sju tingene skjer i
denne rekkefølgen:

1. issue-reservasjon i Azure-workeren,
2. rent arbeidsområde,
3. eksplisitt kontekst,
4. avgrensede modellsteg med plan-gate,
5. krav om faktisk diff,
6. bygg og test,
7. levering som pull request fra Azure.

## Før du begynner

Ha dette klart:

- Skrivetilgang til `novanet/workshop.oslo-live`: godta collaborator-invitasjonen og lag et [klassisk GitHub-token](https://github.com/settings/tokens/new?scopes=public_repo&description=oslo-live-workshop) med bare **public_repo** og kort utløp. Fine-grained funker ikke for collaborators utenfor Novanet-orgen.
- Et agentnavn, for eksempel `kari`: 3–12 tegn, små bokstaver, tall og bindestrek, og det må starte med en bokstav.
- `mini-nils.zip`, pakket ut i mappen der du skal jobbe.
- `pwsh` 7, .NET 10 SDK, Git, `gh` og Python 3. Devcontainer eller Codespace er fallback.
- Mot slutten av labben gir kurslederen deg `AZURE_CREDENTIALS`,
  `ANTHROPIC_API_KEY` og `WEBHOOK_SECRET`. Du trenger ingen Anthropic-nøkkel
  lokalt.

Du har med to repoer å gjøre:

| Repo | Innhold | Hvem eier det |
|---|---|---|
| **Ditt eget** | Agenten, oppskriften og Azure-oppsettet | Du |
| **[novanet/workshop.oslo-live](https://github.com/novanet/workshop.oslo-live)** | Oppgavene og koden agenten skal endre | Kurset |

### 1. Lag repoet ditt

Last ned og pakk ut starter-kitet, og åpne `pwsh` i mappen:

```powershell
Invoke-WebRequest https://novanet.github.io/workshop.oslo-live/mini-nils.zip -OutFile mini-nils.zip
Expand-Archive mini-nils.zip -DestinationPath .
Set-Location mini-nils
```

Så lager du repoet:

```powershell
git init -b main
git add -A
git commit -m "Start fra delene til Mini-Nils"
gh repo create mini-kari --private --source . --push
```

Bytt `kari` med navnet ditt. Pushen starter deploy-workflowen, men den hopper
over med en notis fordi `AGENT_NAME` ikke er satt ennå. Det er som det skal.

### 2. Fyll inn miljøet

```powershell
Copy-Item .env.example .env
```

Sett:

```dotenv
AGENT_NAME=kari
GH_TOKEN=ghp_...
```

`GH_TOKEN` er et klassisk token med bare `public_repo`. Lab 1 bruker bare
lesende `issue view` i dry-run. I Lab 2 skal Azure-workeren tildele issue,
skrive kommentarer, pushe og opprette PR, og da må du være collaborator med
Write på `novanet/workshop.oslo-live`. Hvis workeren i Lab 2 logger `Resource
not accessible by personal access token (addComment)` eller
`replaceActorsForAssignable`, sjekk først at du har godtatt
collaborator-invitasjonen, deretter at tokenet har `public_repo`.

Du trenger ikke `ANTHROPIC_API_KEY` lokalt. Dry-run kaller ikke Claude. Kurset
har én felles nøkkel som du legger inn som repository secret på slutten av
labben. Ikke kjøp API-kreditt selv; Claude.ai-abonnement og Anthropic
API-betaling er separate tjenester.

Sjekk verktøyene:

```powershell
dotnet --version
gh --version
python --version
dotnet build
```

Claude Code trengs ikke lokalt. Det ligger i worker-imaget i Azure.

## Sjekk oppsettet først

Kjør først en dry-run. Den bruker GitHub-tokenet fra `.env`, men reserverer
ikke issuen, kommenterer ikke og trenger ikke `ANTHROPIC_API_KEY`:

```powershell
dotnet run --project src/Worker -- `
  --issue 1 `
  --repo novanet/workshop.oslo-live `
  --dry-run
```

Når dry-run fungerer, skal du ikke kjøre workeren videre lokalt. Direkte
issue-kjøring uten `--dry-run` er blokkert i starter-kitet, slik at ingen kan
reservere issue, skrive kommentarer, pushe eller opprette PR før agenten er
deployet og webhooken er koblet til.

Den første kjøringen som kan reservere issue, skrive start- og sluttkommentarer
og lage PR, kommer fra køen i Azure i Lab 2.

## Start med skjelettet

Kjør:

```powershell
python setup-lab1.py
dotnet build
```

Scriptet legger inn et skjelett i `src/Worker/Pipeline.cs` og tar vare på
det som stod der fra før i `.kurs/Pipeline.forsok.cs`. Skjelettet har den
ytterste `Run`-metoden og alle hjelpeklassene du trenger, men `RunStages` er
din oppgave. Dry-run håndteres i `Run` før `RunStages`, så du trenger ikke
tenke på den i oppskriften under.

Ikke åpne sjekkpunktet ennå. Det er den ferdige løsningen som skal brukes
først hvis du faller av.

## Slik gjør du det i `RunStages`

Implementer ett steg om gangen. Kjør `dotnet build` etter hvert steg. Bygget
viser at du har koblet sammen riktige typer; loggen og kjøringen viser om
oppførselen er riktig.

`RunStages` i skjelettet har én TODO per steg under, inkludert 4b, og en
TODO 0 om å fjerne plassholderen. TODO-en sier hva, stegene her sier hvordan.
Les steget før du skriver koden.

### 1. Kontroller issue-status før modellen

Hvis `skipReservation` er satt, skal du logge at statuskontrollen hoppes over. Ellers:

- kall `Reservation.IsBusy(task, AgentName)`,
- hvis resultatet ikke er `null`, kommenter på issuen hvorfor agenten lar den ligge og returner `0`,
- hvis issuen er åpen, kall `Reservation.Claim(task)`. Dette er en synlig tildeling,
  ikke en eksklusiv lås; flere deltakere kan tildele seg samme issue.

Kontrollen skal skje før repoet klones og før modellen bruker tokens. Første
mergete PR vinner, og Sensor avviser konkurrerende PR-er etterpå.

### 2. Lag et rent arbeidsområde

- kall `Repository.Clone(task.Repo, workspace)`,
- lag en branch med `repo.CreateBranch(task.BranchName)`,
- logg branchen,
- kommenter på issuen med `StartMarker(task)`.

Resten av kjøringen skal bruke `repo.Path`. Modellen skal aldri jobbe direkte
på agentrepoet eller på `main`.

### 3. Bygg konteksten

- installer skills med `InstallSkills()`,
- les agentens hukommelse med `ReadMemory()`,
- bygg systemprompten med `BuildSystemPrompt(memory)`,
- start `previousOutput` som en tom streng.

Hukommelse, regler og konvensjoner er input til modellen. De skal ikke skjules
i en voksende samtalehistorikk.

### 4. Kjør de konfigurerte modellstegene

Gå gjennom `config.Stages`. For hvert steg:

- bruk `stage.Model` når den er satt, ellers `config.Model`,
- bygg prompten med `BuildPrompt(stage, task, previousOutput)`,
- kall `ClaudeRunner.Run(stage, model, prompt, systemPrompt, repo.Path)`,
- legg resultatet i `runs` som en `StageRun`,
- logg forbruket med `LogUsage`.

Hvis resultatet ikke er vellykket, skal du kommentere feilen med `Fence`,
`UsageTable(runs)` og `EndMarker`, og returnere `1`. Ikke kjør neste steg.
Hvis resultatet er vellykket, skal teksten bli `previousOutput` til neste steg.

Det er konfigurasjonen som bestemmer modell, verktøy og maks antall turer.
Starter-kitet har fire modellsteg i `agent/stages.json`:

| Steg | Modell |
|---|---|
| `research` | `claude-opus-5-5` |
| `plan-review` | `claude-haiku-4-5` |
| `implement` | `claude-sonnet-5` |
| `self-review` | `claude-sonnet-5` |

Etter de fire stegene kommer levering: bygg, test, commit, push og PR. Det er
kode i workeren, ikke modell. Modellen skal ikke få velge neste steg selv.

### 4b. Plan-gaten

Rett etter `plan-review` sjekker du svaret. Hvis teksten ikke starter med
`PLAN GODKJENT`:

- kommenter svaret med `Fence`, `UsageTable(runs)` og
  `EndMarker(task, "plan ikke godkjent", pr: false, runs)`,
- kall `Remember(task, "stoppet, plan ikke godkjent", runs)`,
- returner `1`.

Implement skal aldri starte på en plan som ikke er godkjent.

### 5. Sjekk at agenten har laget en diff

Kall `repo.HasChanges()` etter modellstegene. Hvis det ikke finnes endringer:

- kommenter agentens siste svar på issuen,
- ta med forbruk og `EndMarker(task, "ingen endring", pr: false, runs)`,
- kall `Remember(task, "ingen kodeendring", runs)`,
- returner `0`.

En god forklaring uten kodeendring er ikke en leveranse og skal ikke bli en
tom pull request.

### 6. Kjør bygg og test

Kall `Verify(repo.Path)` og ta vare på både `verified` og `verifyLog`.

Hvis verifiseringen feiler og `config.StopOnVerifyFailure` er satt:

- logg feilen,
- kommenter resultatet med `Fence(verifyLog, 2000)`, forbruk og `EndMarker`,
- kall `Remember(task, "stoppet, rødt bygg", runs)`,
- returner `2` uten å opprette en PR.

Standard er `"stopOnVerifyFailure": false`. Da går du videre og leverer PR-en
som utkast. Verifisering i kode er en port. At modellen sier at det virker, er
ikke en port.

### 7. Lever endringen som en pull request

- kall `repo.CommitAndPush(task.BranchName, ...)`,
- bygg PR-teksten med `PrBody(task, previousOutput, verified, verifyLog, runs)`,
- kall `repo.CreatePullRequest(...)`,
- bruk `draft: !verified`,
- kommenter PR-adressen, forbruket og `EndMarker`,
- skriv sammendrag med `WriteSummary`,
- kall `Remember` med utfallet,
- returner `0`.

Agenten leverer et forslag. Den merger aldri selv; PR-en går til review, og et
menneske merger.

## Bygg og kjør dry-run

```powershell
dotnet build
dotnet run --project src/Worker -- `
  --issue 1 `
  --repo novanet/workshop.oslo-live `
  --dry-run
```

Dry-run leser issue-status og stopper før reservasjon, clone, issue-kommentar,
Claude Code, commit, push og PR. Den bekrefter at tokenet kan lese oppgaven,
men gjør ingen endring i GitHub.

Commit og push koden din når bygget er grønt. Det er dette som blir deployet:

```powershell
git add -A
git commit -m "Implementer RunStages"
git push
```

## Før lunsj: secrets og første deploy

Deployen skriver ingenting til issues i Oslo Live. Den lager bare dine
ressurser i Azure, så grensen mellom lokal kjøring og sky holder fortsatt.

### 1. Legg inn secrets

Kurslederen gir deg `ANTHROPIC_API_KEY`, `WEBHOOK_SECRET` og
`AZURE_CREDENTIALS`. `GH_TOKEN` er tokenet du laget selv. Kjør fra mappen til
agentrepoet. `gh` ber deg lime inn verdien, så den havner ikke i
terminalhistorikken:

```powershell
gh secret set GH_TOKEN
gh secret set ANTHROPIC_API_KEY
gh secret set WEBHOOK_SECRET
```

`AZURE_CREDENTIALS` er JSON over flere linjer. Lim den inn i nettleseren:
**Settings → Secrets and variables → Actions → New repository secret**. Den
gir Contributor på den delte ressursgruppen `rg-agentic-workshop`. Bruk den
bare i GitHub Actions, aldri lokalt, og ikke del den videre.

### 2. Sett agentnavnet

```powershell
gh variable set AGENT_NAME --body kari
gh secret list
gh variable list
```

Bruk samme navn som i `.env`. Listene skal vise fire secrets og `AGENT_NAME`.
Ingen andre trengs.

### 3. Kjør workflowen

Åpne **Actions → Build and deploy Mini-Nils → Run workflow**. Workflowen
logger inn med `AZURE_CREDENTIALS`, oppretter ressursene dine, bygger bildene
i kursets felles ACR og deployer. Første gang tar det rundt 10 minutter.

Du skal ikke logge inn i Azure, kjøre `docker login` eller kjøre deploy-skriptet lokalt.

### 4. Si fra til kurslederen

Åpne den ferdige kjøringen og velg **Summary**. Der står `Receiver URL`,
`Issue label` (`kari`) og `Worker job`. Når `Receiver URL` vises, sier du fra
til kurslederen. Kurslederen kobler webhooks fortløpende fra ca. 11:30 og
resten i lunsjen.

Er webhooken din koblet før du går til lunsj, merker du en issue med én gang
(se Lab 2, steg 2). Da jobber agenten mens du spiser, og Lab 2 starter med en
PR å lese i stedet for å vente.

> **Sjekkpunkt 1:** Din egen implementasjon av `RunStages` bygger grønt,
> dry-run er ren, og første deploy fra Actions er grønn med `Receiver URL` i
> Summary.

## Hvis du trenger hjelp

Forsøket ditt blir tatt vare på før den ferdige løsningen kopieres inn:

```powershell
./sjekkpunkt.ps1 lab1
dotnet build
```

Virker ikke skriptet, kjør `python setup-lab1.py --checkpoint`. Du kan se
forskjellen mellom forsøket og løsningen slik:

```powershell
code --diff .kurs\Pipeline.forsok.cs src\Worker\Pipeline.cs
```

Den ferdige pipelinen er et redningsnett. Les den etterpå og finn ut hvilket
steg du manglet. Commit, push og kjør deployen før lunsj uansett.

## Hvis du blir tidlig ferdig

Gjør én liten endring i din egen pipeline og mål effekten:

- flytt en kontroll og forklar hvorfor den fortsatt skjer på samme måte,
- endre hva som skjer når verifisering feiler,
- logg en ekstra måling i PR-rapporten,
- eller la et modellsteg få mindre verktøytilgang.

Dokumenter hva som ble bedre, hva som ble dårligere og hvilken måling som
viser forskjellen.
