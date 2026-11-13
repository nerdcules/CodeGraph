# Rust 1.70 with the rust-analyzer of its day, so an old indexer's SCIP goes through the importer.
FROM rust:1.70-bookworm
COPY --from=mcr.microsoft.com/dotnet/aspnet:10.0 /usr/share/dotnet /usr/share/dotnet
RUN ln -s /usr/share/dotnet/dotnet /usr/local/bin/dotnet \
    && apt-get update && apt-get install -y --no-install-recommends python3 && rm -rf /var/lib/apt/lists/* \
    && (rustup component add rust-analyzer || rustup component add rust-analyzer-preview) \
    && rust-analyzer --version
ENV DOTNET_ROOT=/usr/share/dotnet DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
