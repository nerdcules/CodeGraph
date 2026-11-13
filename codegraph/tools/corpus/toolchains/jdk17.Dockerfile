# Temurin JDK 17 and Maven; scip-java is mounted from the host and runs on this JDK.
FROM maven:3.9-eclipse-temurin-17
COPY --from=mcr.microsoft.com/dotnet/aspnet:10.0 /usr/share/dotnet /usr/share/dotnet
RUN ln -s /usr/share/dotnet/dotnet /usr/local/bin/dotnet \
    && apt-get update && apt-get install -y --no-install-recommends python3 && rm -rf /var/lib/apt/lists/*
# This Maven's default plugins and the corpus's JUnit, fetched now so the runs work offline (run_one.sh copies
# /opt/m2-seed to the runner's ~/.m2 before the host's cache, whose plugin versions are the host Maven's).
COPY maven-warm /opt/maven-warm
RUN cd /opt/maven-warm && mvn -B -q -Dmaven.repo.local=/opt/m2-seed/repository clean verify \
    && chmod -R a+rX /opt/m2-seed
ENV DOTNET_ROOT=/usr/share/dotnet DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
