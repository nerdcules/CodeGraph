# Real Obsidian, headless, with the CodeGraph plugin: one container per Obsidian version.
#
# Builds on a local image with the .NET 10 SDK, Node 22+ and Python 3 (the corpus base), adds the libraries
# Electron needs and Xvfb, and unpacks the Linux build of Obsidian. Network is needed at build time only; run.py
# starts the containers with --network none, which also stops Obsidian from fetching a newer app.asar, so the
# version tested is the version named here. Obsidian is not open source: build this image locally, never publish it.
#
#   docker build -t codegraph-obsidian:1.8.9 --build-arg BASE=foundry-field-test:none --build-arg OBSIDIAN_VERSION=1.8.9 tools/obsidian-e2e
ARG BASE=mcr.microsoft.com/dotnet/sdk:10.0
FROM ${BASE}
ARG OBSIDIAN_VERSION=1.8.9
USER root
RUN apt-get update && apt-get install -y --no-install-recommends \
      xvfb xauth ca-certificates curl python3 \
      libgtk-3-0t64 libnss3 libgbm1 libasound2t64 libxss1 libxtst6 libdrm2 libatk-bridge2.0-0t64 libsecret-1-0 \
      libnotify4 libxkbfile1 libuuid1 fonts-dejavu-core \
    && rm -rf /var/lib/apt/lists/* \
    && (command -v node >/dev/null || (apt-get update && apt-get install -y --no-install-recommends nodejs && rm -rf /var/lib/apt/lists/*))
RUN curl -fsSL "https://github.com/obsidianmd/obsidian-releases/releases/download/v${OBSIDIAN_VERSION}/obsidian-${OBSIDIAN_VERSION}.tar.gz" \
      -o /tmp/obsidian.tar.gz \
    && mkdir -p /opt/obsidian && tar -xzf /tmp/obsidian.tar.gz -C /opt/obsidian --strip-components=1 && rm /tmp/obsidian.tar.gz \
    && test -x /opt/obsidian/obsidian \
    && mkdir -p /home/runner /results /opt/corpus /opt/e2e && chmod 1777 /home/runner /results
ENV HOME=/home/runner OBSIDIAN_VERSION=${OBSIDIAN_VERSION} \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
    CODEGRAPH_TS=/opt/codegraph/extractors/typescript/cli.mjs
COPY run_e2e.sh e2e.mjs cdp.mjs /opt/e2e/
RUN chmod 755 /opt/e2e/run_e2e.sh
ENTRYPOINT ["/opt/e2e/run_e2e.sh"]
