# syntax=docker/dockerfile:1

# Two images out of one build: the API and a one-shot migrator. They share every layer up to
# `build`, so the second costs a copy rather than a second compile.

ARG DOTNET_VERSION=8.0

# ---------------------------------------------------------------------------- build --------

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /src

# Project files first, restore, then the sources, so that editing a .cs file leaves the
# restore layer cached.
COPY Directory.Build.props ./
COPY src/OrderTracking.Domain/OrderTracking.Domain.csproj src/OrderTracking.Domain/
COPY src/OrderTracking.Infrastructure/OrderTracking.Infrastructure.csproj src/OrderTracking.Infrastructure/
COPY src/OrderTracking.Api/OrderTracking.Api.csproj src/OrderTracking.Api/
RUN dotnet restore src/OrderTracking.Api/OrderTracking.Api.csproj

COPY src/ src/

RUN dotnet publish src/OrderTracking.Api/OrderTracking.Api.csproj \
      --configuration Release \
      --no-restore \
      --output /app/api

# A migration bundle: one executable carrying the migrations and enough of EF Core to apply
# them, so the migrator image needs no SDK. Framework-dependent and with no runtime
# identifier, so it runs on whatever architecture the runtime image was pulled for.
RUN dotnet tool install --global dotnet-ef --version 8.* \
 && /root/.dotnet/tools/dotnet-ef migrations bundle \
      --project src/OrderTracking.Infrastructure \
      --startup-project src/OrderTracking.Api \
      --configuration Release \
      --output /app/migrate/efbundle

# ------------------------------------------------------------------------------ api --------

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS api

# curl is here for the compose healthcheck and nothing else.
RUN apt-get update \
 && apt-get install --yes --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/api ./

# The rolling file sink writes here. The directory has to exist and belong to the unprivileged
# user before the switch below, because afterwards nothing can create it.
RUN mkdir --parents /app/logs && chown $APP_UID /app/logs

# After the apt-get above, which needs root, and before anything that does not.
USER $APP_UID

EXPOSE 8080
ENTRYPOINT ["dotnet", "OrderTracking.Api.dll"]

# ------------------------------------------------------------------------- migrator --------

# The ASP.NET image rather than the smaller runtime one: the bundle builds the startup
# project's host to locate the DbContext, so it inherits that project's framework references
# and will not start without Microsoft.AspNetCore.App.
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS migrator
WORKDIR /app
COPY --from=build /app/migrate ./
USER $APP_UID

# No arguments: the bundle resolves its context through OrderTrackingDbContextFactory, which
# reads ORDERTRACKING_MIGRATIONS_CONNECTION. Passing --connection instead would put the
# password in the process list.
ENTRYPOINT ["./efbundle"]
