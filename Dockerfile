# syntax=docker/dockerfile:1

# ---- Build the React client ----
FROM node:22-alpine AS client-build
WORKDIR /src/client
COPY ChitChat.Client/package.json ChitChat.Client/package-lock.json ./
RUN npm ci
COPY ChitChat.Client/ ./
RUN npm run build

# ---- Build and publish the ASP.NET Core server ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS server-build
WORKDIR /src
COPY ChitChat.Server/ChitChat.Server.csproj ChitChat.Server/
RUN dotnet restore ChitChat.Server/ChitChat.Server.csproj
COPY ChitChat.Server/ ChitChat.Server/
RUN dotnet publish ChitChat.Server/ChitChat.Server.csproj -c Release -o /app/publish --no-restore

# ---- Final runtime image ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=server-build --chown=app:app /app/publish .
COPY --from=client-build --chown=app:app /src/client/dist ./wwwroot

# Serilog's rolling file sink writes to logs/ under the content root; /app itself is owned by
# root, so the non-root app user needs its own writable folder there.
RUN mkdir -p /app/logs && chown app:app /app/logs

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER app

ENTRYPOINT ["dotnet", "ChitChat.Server.dll"]
