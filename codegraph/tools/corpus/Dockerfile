# One container per (language, profile): generate a codebase, index it with codegraph-index, score the graph.
#
# The default base is the official .NET 10 SDK image with Node, Python, a JDK and Maven installed from apt
# (INSTALL_TOOLS=1). run.py instead builds on an existing local image and mounts the host's JDK, Maven,
# scip-java and caches, so a run needs no downloads and the containers run with --network none.
#
#   docker build -t codegraph-corpus tools/corpus
#   docker build -t codegraph-corpus --build-arg BASE=my-dotnet-node-image --build-arg INSTALL_TOOLS=0 tools/corpus
ARG BASE=mcr.microsoft.com/dotnet/sdk:10.0
FROM ${BASE}
ARG INSTALL_TOOLS=1
USER root
RUN if [ "$INSTALL_TOOLS" = "1" ]; then \
      apt-get update && apt-get install -y --no-install-recommends python3 nodejs openjdk-21-jdk-headless maven \
      && rm -rf /var/lib/apt/lists/*; \
    fi \
    && mkdir -p /home/runner /opt/corpus /results && chmod 1777 /home/runner /results
ENV HOME=/home/runner \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
    CODEGRAPH_TS=/opt/codegraph/extractors/typescript/cli.mjs \
    MAVEN_ARGS=--offline
COPY run_one.sh gen.py check.py vault_check.py live.py live_check.cjs /opt/corpus/
RUN chmod 755 /opt/corpus/run_one.sh
ENTRYPOINT ["/opt/corpus/run_one.sh"]
