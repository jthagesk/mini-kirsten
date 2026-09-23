# Lab 2: Til skyen

**Mål:** en issue merket i GitHub gir en pull request fra Azure, uten at
laptopen din er med.

Agenten din ble deployet før lunsj, og kurslederen har koblet webhooken.
Merket du en issue før lunsj, har agenten kanskje allerede levert. Hopp da
rett til steg 4.
Dette er første live-kjøring: Azure-workeren skal tildele issue, skrive start-
og sluttkommentarer, pushe branchen og opprette PR. Ikke gjør noen av disse
handlingene manuelt.

Alle kommandoer er skrevet for PowerShell 7 (`pwsh`).

**Tid:** 13:00–14:00.

## Før du begynner

- Sjekkpunkt 1 er nådd: `RunStages` bygger grønt, eller du har kjørt
  `./sjekkpunkt.ps1 lab1`, og første deploy fra Actions er grønn.
- Du har `Receiver URL` og `Issue label` fra **Actions → Summary**. Med
  `AGENT_NAME=kari` er etiketten `kari`.
- Kurslederen har koblet webhooken din til Oslo Live, før eller i lunsjen.

## Hva som kjører i Azure

```
GitHub-issue i Oslo Live (etiketten til agenten din: kari)
  → Mottak      Container App, minReplicas 1 under kurset, sjekker signatur, legger melding i kø
  → Kø          Storage Queue
  → Worker      Container Apps Job, skalerer til null, KEDA starter én kjøring per melding
  → PR          i Oslo Live
```

Mottaket har `minReplicas: 1` under kurset, så GitHub ikke venter på
kaldstart. GitHub gir opp en webhook etter 10 sekunder. Workeren skalerer
fortsatt til null.

Receiver og worker godtar bare `TARGET_REPO`, som er
`novanet/workshop.oslo-live`. Meldinger om andre repoer avvises.

Dette satte du i Lab 1, og det er hele listen:

| GitHub-innstilling | Navn | Bruk |
|---|---|---|
| Repository variable | `AGENT_NAME` | Agentnavnet, for eksempel `kari`. Gir etikett, Azure-navn og ACR-namespace. |
| Repository secret | `AZURE_CREDENTIALS` | Contributor på den delte ressursgruppen. Bare i GitHub Actions. |
| Repository secret | `GH_TOKEN` | Ditt klassiske token med `public_repo`. |
| Repository secret | `ANTHROPIC_API_KEY` | Kursets felles Anthropic-nøkkel. |
| Repository secret | `WEBHOOK_SECRET` | Samme verdi som webhooken er registrert med. |

Workflowen sender `GH_TOKEN`, `ANTHROPIC_API_KEY` og `WEBHOOK_SECRET` som
secure parametere til Bicep. De lagres ikke i Key Vault, ACR eller image-lag.

## Hva er KEDA?

KEDA betyr **Kubernetes Event-driven Autoscaling**. Azure Container Apps bruker
KEDA som en hendelsesstyrt skaleringskomponent: den følger med på antall meldinger i
Storage Queue og starter Container Apps Job når køen har arbeid.

I dette oppsettet:

- `minExecutions: 0` betyr at jobben kan stå helt stille når køen er tom.
- `pollingInterval: 30` betyr at KEDA normalt sjekker køen hvert 30. sekund.
- `queueLength: 1` sier at én melding er nok til å utløse en kjøring.
- Workeren henter én melding, løser oppgaven og avslutter. Den kjører ikke en
  evig loop.
- `maxExecutions: 3` begrenser hvor mange kjøringer skalaen kan starte.

KEDA er altså ikke mottaket, køen eller selve workeren. Det er koblingen som
gjør at en melding i køen kan starte en kortvarig worker-kjøring. Derfor er
opptil 30 sekunders venting etter en webhook normalt.

Merk hvor ting bor. Mottaket og workeren er dine, bygget fra ditt repo, men
ligger i kursets felles ressursgruppe med navn avledet fra `AGENT_NAME`.
Container Registryet er felles for kurset, men bildene ligger under hver sin
sti:

```text
<kursregistry>.azurecr.io/mini-nils/kari/receiver:<tag>
<kursregistry>.azurecr.io/mini-nils/kari/worker:<tag>
```

Hver deltakers deploy-serviceprincipal får `AcrPush` og `Container Registry
Tasks Contributor` på det felles registryet, men workflowen bruker
agentetiketten i image-navnet slik at Kari og Nils ikke overskriver hverandre.
Container Apps trekker bilder med en kurs-eid managed identity som bare har
`AcrPull`. `AcrPush` er en registry-omfattende rolle, så namespace-regelen er
en kurskonvensjon og ikke en hard sikkerhetsgrense; bruk bare denne modellen i
en kontrollert kurs-tenant.

Issuen og pull requesten er i Oslo Live, der alle agentene på kurset jobber.

### Hvorfor en egen etikett?

Alle mottakerne på kurset får **alle** issue-hendelser fra Oslo Live. Lyttet
alle etter samme etikett, ville alle agentene startet hver gang noen merket en
issue. Og alle ville betalt for det.

Så etiketten er agentnavnet ditt: med `AGENT_NAME=kari` lytter agenten etter
`kari`. Workflowen lager den av `AGENT_NAME` og hopper over deployen med en
notis hvis variabelen mangler. Mottaket ser alle hendelsene, men bryr seg bare
om sin egen etikett.

Setter flere agenter etiketten sin på samme issue, kan alle starte og levere
hver sin branch og PR. Den første PR-en som merges vinner; Sensor ber de andre
åpne PR-ene om endringer når issuen allerede er løst.

## 1. Sjekk at receiveren svarer

Kopier `Receiver URL` fra **Actions → Summary**. `.deploy-outputs.json` ligger
bare på den midlertidige Actions-runneren og finnes ikke lokalt.

```powershell
$ReceiverUrl = 'https://ca-...azurecontainerapps.io'
Invoke-RestMethod "$ReceiverUrl/"
# Mini-Nils receiver. Listening for the «kari» label.
```

`curl.exe "$ReceiverUrl/"` gir det samme.

## 2. Merk en issue med etiketten din

Åpne en issue i [Oslo Live](https://github.com/novanet/workshop.oslo-live/issues).
Velg en av **Feil 2 til 5** som ingen har tatt ennå, og sett etiketten din på
den, i nettleseren eller slik:

```powershell
gh issue edit 3 --repo novanet/workshop.oslo-live --add-label kari
```

Bytt `3` med issuen du valgte. Ikke tildel issuen, skriv startkommentar eller
opprett PR selv.

## 3. Mens du venter

En kjøring tar typisk 10 til 20 minutter. Bruk tiden på to ting:

- **Velg Lab 3-spor** og gjør endringen lokalt. Ikke push før jobben din er
  ferdig, så den nye deployen ikke kommer midt i kjøringen.
- **Test at mottaket avviser feil hemmelighet:**

```powershell
$Body = '{"repo":"novanet/workshop.oslo-live","issueNumber":2}'
curl.exe -i -X POST "$ReceiverUrl/tasks" `
  -H "X-Webhook-Secret: deliberately-wrong" `
  -H "Content-Type: application/json" `
  --data-raw $Body
```

Forvent `401 Unauthorized`. Dette starter ingen jobb. Finn kontrollen i
`src/Receiver/Program.cs`. `/tasks` krever hemmeligheten i en header, og
webhooken fra GitHub er signert med HMAC. Selve kømeldingen er ikke signert,
så mottaket er porten.

## 4. Følg jobben i issue-kommentarene

```powershell
gh issue view 3 --repo novanet/workshop.oslo-live --comments
```

KEDA sjekker køen hvert 30. sekund, så litt venting er normalt. Først kommer
startkommentaren fra agenten, så sluttkommentaren med PR-adresse og
forbrukstabell.

Lukk laptopen. Gå og hent kaffe. Kom tilbake og se at PR-en er der.

Worker-loggen i Azure kan kurslederen vise. Ved feilsøking kan kurslederen
midlertidig sette `CLAUDE_VERBOSE=true` i `env:`-blokken øverst i
`.github/workflows/deploy-azure.yml` før en ny workflow-kjøring. Da sendes
Claude Code sitt `--verbose`-flagg, og diagnostikken skrives fortløpende i
samme worker-logg. Sett verdien tilbake eller fjern den etter feilsøkingen.

## 5. Les PR-en

```powershell
gh pr list --repo novanet/workshop.oslo-live --author "@me"
```

Åpne PR-en og les rapporten og forbrukstabellen. Agenten merger ikke selv;
PR-en venter på review fra et menneske. Var bygget rødt, er PR-en et utkast.

> **Sjekkpunkt 2:** En merket issue gir en pull request opprettet av
> Azure-jobben.

## 6. Når Sensor sier nei

Sensor leser PR-en mot akseptansekriteriene i issuen og kjører de skjulte
testene. Mangler noe, får PR-en **Request changes** med en tabell over hva som
feiler.

Agenten din leser ikke reviewen. Den kjører én gang per etikett og står stille
etterpå. Neste trekk er ditt:

1. Les tabellen. Hva bommet agenten på?
2. Fiks agenten, ikke koden i PR-en. Stram inn en prompt i `agent/prompts/`,
   `agent/conventions.md` eller en skill. Commit, push og vent til deployen er
   grønn.
3. Ta etiketten av og sett den på igjen:

```powershell
gh issue edit 3 --repo novanet/workshop.oslo-live --remove-label kari
gh issue edit 3 --repo novanet/workshop.oslo-live --add-label kari
```

Agenten starter fra `main` igjen, pusher til samme branch og oppdaterer PR-en
du allerede har. Sensor vurderer den nye commiten.

Agenten ser bare issuen og hukommelsen sin, ikke det Sensor skrev. Endrer du
ikke agenten, gjør den trolig samme feil en gang til. Vil du at den skal lese
reviewen sjøl, er det en oppgave i Spor G i Lab 3.

Sier Sensor at issuen allerede er løst av en annen PR, er det ingen vits å
kjøre på nytt. Velg en ny issue.

## Se kostnaden i Log Analytics

Kurslederen kan vise dette i Azure-portalen: ressursgruppen → Log Analytics
workspace → **Logs**:

```kusto
ContainerAppConsoleLogs_CL
| where ContainerName_s == "worker"
| where Log_s contains "LLM request completed"
| project TimeGenerated, Log_s
| order by TimeGenerated desc
```

Dette er den samme linjen Nils Georg skriver for hver LLM-kjøring. Alt kostnadsregnskap i Cato bygger på den.

`cost_usd` er Claude Codes egen utregning. Står det `cost_estimated=true`, har
workeren regnet ut kostnaden selv fra tokens og listepris, fordi steget ble
stoppet av tidsgrensen eller Claude Code ikke hadde pris for modellen. I
forbrukstabellen i PR-en vises slike beløp med `~`.

## Hvis du blir tidlig ferdig

Gjør én liten endring i `infra/main.bicep` som krever ny deploy:

- endre `pollingInterval` i KEDA-regelen fra `30` til `15`,
- commit og push til `main`. Deployen starter av seg selv i Actions,
- be kurslederen bekrefte den nye verdien i Azure.

Du ber KEDA sjekke køen oftere. Du endrer ikke hvordan workeren behandler
meldingen.

Åpne også `infra/main.bicep` og finn

- hvor KEDA-regelen som starter jobben står,
- hvor repository secrets blir til jobbens miljøvariabler,
- hvorfor mottaket har `minReplicas: 1` under kurset.

## Hvis noe stopper

**Webhooken når ikke fram.** Ikke legg `WEBHOOK_SECRET` i terminalhistorikk
eller del den i en issue. Be kurslederen kontrollere **Recent deliveries** i
GitHub og redelivere webhooken med kurslederens oppsett.

**Deploy feiler på rettigheter.** Kontroller først at `AZURE_CREDENTIALS`
finnes som repository secret og at `AGENT_NAME` er satt. Hvis feilen
fortsetter, si fra til kurslederen; service-principalen må få rettighetene sine
rettet sentralt.

**Jobben starter, men feiler med en gang.** Be kurslederen hente
worker-loggen. Mangler det en miljøvariabel, ligger feilen i
`infra/main.bicep` under `env:`.

## Behold miljøet til Lab 3

Lab 3 bygger videre på denne deployen og bruker samme Container Apps Job,
Storage Queue, webhook og Azure-logger. Gjør endringer i agenten lokalt, push
dem, og deployen kjører fra Actions.

Ressursgruppen er delt av hele kurset. Ikke slett noe. Kurslederen rydder
etter kurset.
