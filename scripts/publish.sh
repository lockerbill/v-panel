#!/usr/bin/env bash
# Builds a self-contained linux binary into ./publish (copy it together with ./deploy to the server).
set -euo pipefail
cd "$(dirname "$0")/.."
RID="${1:-linux-x64}"
dotnet publish src/VPanel.Web/VPanel.Web.csproj -c Release -r "$RID" --self-contained true \
  -p:PublishSingleFile=false -p:DebugType=none -o publish
echo "Published to ./publish ($RID). Install on the server with: sudo ./deploy/install.sh ./publish"
