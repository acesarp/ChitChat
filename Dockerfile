# syntax=docker/dockerfile:1

# ---- Build the React client ----
FROM node:22-alpine AS client-build
WORKDIR /src/client
COPY Sample_App.Client/package.json Sample_App.Client/package-lock.json ./
RUN npm ci
COPY Sample_App.Client/ ./
RUN npm run build

# ---- Build and publish the ASP.NET Core server ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS server-build
WORKDIR /src
COPY SampleApp.Server/SampleApp.Server.csproj SampleApp.Server/
RUN dotnet restore SampleApp.Server/SampleApp.Server.csproj
COPY SampleApp.Server/ SampleApp.Server/
RUN dotnet publish SampleApp.Server/SampleApp.Server.csproj -c Release -o /app/publish --no-restore

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

ENTRYPOINT ["dotnet", "SampleApp.Server.dll"]
