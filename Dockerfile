# syntax=docker/dockerfile:1.7
FROM mcr.microsoft.com/dotnet/sdk:8.0-bookworm-slim AS build
ARG TARGETARCH
WORKDIR /src
COPY . .
RUN set -eu; \
    case "$TARGETARCH" in amd64) rid=linux-x64 ;; arm64) rid=linux-arm64 ;; *) echo "Unsupported architecture: $TARGETARCH" >&2; exit 1 ;; esac; \
    test "$(wc -c < models/BgeSmallZh/bge-small-zh-v1.5.onnx)" -gt 50000000 || { echo "BGE ONNX 模型缺失或仍是 Git LFS 指针" >&2; exit 1; }; \
    dotnet restore TraceSoul2.sln; \
    dotnet publish Tools/Host/TraceSoul2.Host.csproj -c Release -r "$rid" --self-contained false -o /seed/App; \
    dotnet publish Tools/Migration/TraceSoul2.Migrate.csproj -c Release -r "$rid" --self-contained false -o /seed/App; \
    dotnet publish Tools/Updater/TraceSoul2.Updater.csproj -c Release -r "$rid" --self-contained false -o /tmp/updater; \
    cp /tmp/updater/TraceSoul2.Updater* /seed/App/; \
    cp scripts/Start-TraceSoul2.sh /seed/App/; \
    version=$(sed -n 's:.*<TraceSoul2Version>\([^<]*\)</TraceSoul2Version>.*:\1:p' Tools/Directory.Build.props | head -n 1 | tr -d '\r'); \
    printf '{\n  "product": "TraceSoul2",\n  "version": "%s",\n  "runtime": "%s"\n}\n' "$version" "$rid" > /seed/App/tracesoul2.install.json

FROM mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim
WORKDIR /opt/tracesoul2
COPY --from=build /seed/App /opt/tracesoul2-seed/App
COPY scripts/docker-entrypoint.sh /usr/local/bin/tracesoul2-entrypoint
RUN chmod 0755 /usr/local/bin/tracesoul2-entrypoint /opt/tracesoul2-seed/App/Start-TraceSoul2.sh
ENTRYPOINT ["/usr/local/bin/tracesoul2-entrypoint"]
