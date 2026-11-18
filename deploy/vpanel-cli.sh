#!/bin/sh
# Maintenance commands inside the v-panel container, e.g. `docker exec -it vpanel vpanel create-admin`.
# Run `vpanel help` for the list of commands.
exec dotnet /app/VPanel.Web.dll "$@"
