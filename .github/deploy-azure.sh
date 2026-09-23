#!/usr/bin/env bash
# Internal GitHub Actions deployment helper. Participants trigger the workflow.
#
#   bash .github/deploy-azure.sh
# This file is called by GitHub Actions; participants start the workflow.
#
# Reads optional .env values locally, while GitHub Actions supplies the
# repository secrets through the workflow environment.
set -euo pipefail

here="$(cd "$(dirname "$0")/.." && pwd)"
cd "$here"

source "$here/scripts/agent-identity.sh"

if [[ -f .env ]]; then
  set -a
  # shellcheck disable=SC1091
  source .env
  set +a
fi

RESOURCE_GROUP="${RESOURCE_GROUP:-rg-agentic-workshop}"
ACR_NAME="${ACR_NAME:-acragenticworkshop}"
ACR_RESOURCE_GROUP="${ACR_RESOURCE_GROUP:-rg-agentic-workshop}"
ACR_PULL_IDENTITY_RESOURCE_ID="${ACR_PULL_IDENTITY_RESOURCE_ID:-/subscriptions/821c1e3e-f764-4d36-911a-a23a80dfa197/resourceGroups/rg-agentic-workshop/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-agentic-workshop-acr-pull}"
LOCATION="${LOCATION:-norwayeast}"
CLAUDE_MODEL="${CLAUDE_MODEL:-claude-sonnet-5}"
CLAUDE_VERBOSE="${CLAUDE_VERBOSE:-false}"
AGENT_MENTION="${AGENT_MENTION:-}"
# The only repository the agent may work in. Receiver and worker reject others.
TARGET_REPO="${TARGET_REPO:-novanet/workshop.oslo-live}"
# The course key is shared across participants; keep older .env files working
# even when they were copied before this setting was added to .env.example.
ANTHROPIC_WORKSPACE_ID="${ANTHROPIC_WORKSPACE_ID:-wrkspc_014PnTrso2hTWspRTHbJp62c}"

# Your agent identity is the only participant-specific deployment input. It
# appears in commits, PRs, the status page, and the namespaced ACR images.
AGENT_NAME="${AGENT_NAME:-}"
if [[ -z "$AGENT_NAME" || "$AGENT_NAME" == "Mini-" || "$AGENT_NAME" == "Mini-Nils" ]]; then
  echo "Set AGENT_NAME to your unique participant identity." >&2
  exit 1
fi
: "${GH_TOKEN:?Set the GH_TOKEN repository secret}"
: "${ANTHROPIC_API_KEY:?Set the ANTHROPIC_API_KEY repository secret}"
: "${WEBHOOK_SECRET:?Set the WEBHOOK_SECRET repository secret}"

NAME="$(agent_resource_suffix "$AGENT_NAME")"
if [[ ! "$NAME" =~ ^[a-z][a-z0-9-]{2,11}$ ]]; then
  echo "AGENT_NAME must normalize to 3-12 lowercase Azure-safe characters; got: $NAME" >&2
  exit 1
fi

# The label that starts your agent. Every receiver in the workshop gets all
# events from Oslo Live. If they all listened for the same label, every agent
# would start whenever anyone labeled an issue. The label is derived from the
# agent identity.
AGENT_LABEL="$(agent_label_from_name "$AGENT_NAME")"
if [[ "$AGENT_LABEL" == "agent" || "$AGENT_LABEL" == "mini-nils" ]]; then
  echo "Etiketten $AGENT_LABEL deles av alle, og vil starte alle agentene samtidig." >&2
  echo "Choose a unique AGENT_NAME instead." >&2
  exit 1
fi
if [[ ! "$AGENT_LABEL" =~ ^[a-z0-9][a-z0-9._-]*$ ]]; then
  echo "AGENT_LABEL must contain only lowercase letters, numbers, dots, underscores, and hyphens." >&2
  exit 1
fi

mode="${1:-all}"
tag="$(date +%Y%m%d-%H%M%S)"
image_path="mini-nils/$AGENT_LABEL"

if [[ "${GITHUB_ACTIONS:-}" != "true" ]]; then
  echo "Deployments must run from the GitHub Actions workflow." >&2
  exit 1
fi

deploy_infra() {
  local receiver_image="${1:-}"
  local worker_image="${2:-}"
  local extra=()
  [[ -n "$receiver_image" ]] && extra+=("receiverImage=$receiver_image")
  [[ -n "$worker_image" ]] && extra+=("workerImage=$worker_image")

  echo "── Deployer infrastruktur til $RESOURCE_GROUP ($LOCATION)"
  az group create --name "$RESOURCE_GROUP" --location "$LOCATION" --output none
  az deployment group create \
    --resource-group "$RESOURCE_GROUP" \
    --name "mininils-$tag" \
    --template-file infra/main.bicep \
    --parameters name="$NAME" \
                 agentLabel="$AGENT_LABEL" \
                 agentName="$AGENT_NAME" \
                 agentMention="$AGENT_MENTION" \
                 targetRepo="$TARGET_REPO" \
                 githubToken="$GH_TOKEN" \
                 anthropicApiKey="$ANTHROPIC_API_KEY" \
                 webhookSecret="$WEBHOOK_SECRET" \
                 acrName="$ACR_NAME" \
                 acrResourceGroup="$ACR_RESOURCE_GROUP" \
                 acrPullIdentityResourceId="$ACR_PULL_IDENTITY_RESOURCE_ID" \
                 anthropicWorkspaceId="$ANTHROPIC_WORKSPACE_ID" \
                 claudeModel="$CLAUDE_MODEL" \
                 claudeVerbose="$CLAUDE_VERBOSE" \
                 ${extra[@]+"${extra[@]}"} \
    --query properties.outputs \
    --output json > .deploy-outputs.json
}

read_output() {
  python3 -c "import json,sys; print(json.load(open('.deploy-outputs.json'))['$1']['value'])" 2>/dev/null \
    || jq -r ".$1.value" .deploy-outputs.json
}

registry_login_server() {
  az acr show \
    --resource-group "$ACR_RESOURCE_GROUP" \
    --name "$ACR_NAME" \
    --query loginServer \
    --output tsv
}

build_images() {
  local acr="$ACR_NAME"
  echo "── Bygger bilder i $acr (tag $tag)"
  az acr build --registry "$acr" --image "$image_path/receiver:$tag" --file src/Receiver/Dockerfile . --output none
  az acr build --registry "$acr" --image "$image_path/worker:$tag" --file src/Worker/Dockerfile . --output none
}

# True when both the receiver app and the worker job already exist.
resources_exist() {
  az containerapp show \
    --resource-group "$RESOURCE_GROUP" \
    --name "ca-${NAME}-receiver" \
    --output none >/dev/null 2>&1 &&
    az containerapp job show \
    --resource-group "$RESOURCE_GROUP" \
    --name "caj-${NAME}-worker" \
    --output none >/dev/null 2>&1
}

update_existing_images() {
  local login="$1"
  local receiver_name="ca-${NAME}-receiver"
  local worker_name="caj-${NAME}-worker"

  if ! resources_exist; then
    echo "Azure resources are not bootstrapped for $AGENT_NAME." >&2
    echo "Run the GitHub Actions workflow after the course secrets have been provisioned." >&2
    exit 1
  fi

  az containerapp update \
    --resource-group "$RESOURCE_GROUP" \
    --name "$receiver_name" \
    --image "$login/$image_path/receiver:$tag" \
    --output none
  az containerapp job update \
    --resource-group "$RESOURCE_GROUP" \
    --name "$worker_name" \
    --image "$login/$image_path/worker:$tag" \
    --output none
}

case "$mode" in
  --infra)
    deploy_infra
    ;;
  --images)
    build_images
    login="$(registry_login_server)"
    update_existing_images "$login"
    ;;
  all)
    # Bicep creates missing resources and updates existing ones. Only the very
    # first deploy needs a placeholder pass, so the app and job exist before
    # the images are built in the shared ACR. Later deploys build first and
    # deploy once, so the running agent never falls back to the placeholder.
    if resources_exist; then
      echo "── Mottak og worker finnes. Bygger bilder først og deployer én gang."
    else
      echo "── Første deploy: oppretter ressursene med plassholderbilde."
      deploy_infra
    fi
    build_images
    login="$(registry_login_server)"
    deploy_infra "$login/$image_path/receiver:$tag" "$login/$image_path/worker:$tag"
    ;;
  *)
    echo "Ukjent valg: $mode"; exit 1
    ;;
esac

echo
echo "── Ferdig"
echo "Mottak:   $(read_output receiverUrl)"
echo "Jobb:     $(read_output workerJobName)"
echo "Agent:    $AGENT_NAME"
echo "Etikett:  $AGENT_LABEL"
echo "Azure-navnesuffiks: $NAME"
echo
echo "Neste steg: kurslederen registrerer webhooken for deg. Når den er på plass,"
echo "setter du etiketten $AGENT_LABEL på en issue i Oslo Live."
