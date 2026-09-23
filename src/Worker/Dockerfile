# Bygg fra starter-kit-mappen:  az acr build -r <acr> -t mini-nils/<label>/worker:<tag> -f src/Worker/Dockerfile .
#
# The final image uses the SDK image because the worker runs `dotnet build` and `dotnet test`.
# i repoet agenten jobber i. I tillegg trengs git, gh og Claude Code.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Worker/Worker.csproj src/Worker/
RUN dotnet restore src/Worker/Worker.csproj
COPY src/Worker/ src/Worker/
RUN dotnet publish src/Worker/Worker.csproj -c Release -o /out --no-restore

FROM mcr.microsoft.com/dotnet/sdk:10.0

# Pin the version before the workshop so every team runs the same harness.
ARG CLAUDE_CODE_VERSION=latest

RUN apt-get update \
 && apt-get install -y --no-install-recommends git curl ca-certificates gnupg \
 && curl -fsSL https://deb.nodesource.com/setup_22.x | bash - \
 && apt-get install -y --no-install-recommends nodejs \
 && curl -fsSL https://cli.github.com/packages/githubcli-archive-keyring.gpg -o /usr/share/keyrings/githubcli-archive-keyring.gpg \
 && echo "deb [arch=$(dpkg --print-architecture) signed-by=/usr/share/keyrings/githubcli-archive-keyring.gpg] https://cli.github.com/packages stable main" > /etc/apt/sources.list.d/github-cli.list \
 && apt-get update \
 && apt-get install -y --no-install-recommends gh \
 && npm install -g "@anthropic-ai/claude-code@${CLAUDE_CODE_VERSION}" \
 && rm -rf /var/lib/apt/lists/*

# Run as a non-root user. Claude Code rejects some modes when running as root.
RUN useradd --create-home --user-group agent \
 && mkdir -p /workspace \
 && chown agent:agent /workspace

COPY --from=build /out /app
COPY agent/ /app/agent/
RUN chown -R agent:agent /app

USER agent
ENV HOME=/home/agent \
    AGENT_DIR=/app/agent \
    WORKSPACE=/workspace \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

WORKDIR /workspace
ENTRYPOINT ["dotnet", "/app/Worker.dll"]
