# Python 3.8; scip-python (an npm package) is mounted from the host and runs on the Node copied in here.
FROM python:3.8-bookworm
COPY --from=mcr.microsoft.com/dotnet/aspnet:10.0 /usr/share/dotnet /usr/share/dotnet
COPY --from=node:20-bookworm /usr/local/bin/node /usr/local/bin/node
RUN ln -s /usr/share/dotnet/dotnet /usr/local/bin/dotnet && node --version
ENV DOTNET_ROOT=/usr/share/dotnet DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
