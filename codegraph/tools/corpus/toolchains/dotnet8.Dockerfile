# .NET 8 SDK with the .NET 10 runtime beside it: the repo is on net8.0, the indexer is not.
FROM mcr.microsoft.com/dotnet/sdk:8.0
COPY --from=mcr.microsoft.com/dotnet/aspnet:10.0 /usr/share/dotnet /usr/share/dotnet
RUN apt-get update && apt-get install -y --no-install-recommends python3 && rm -rf /var/lib/apt/lists/*
ENV DOTNET_ROOT=/usr/share/dotnet DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
