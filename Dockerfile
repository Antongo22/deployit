FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY DeployIt/DeployIt.csproj DeployIt/
RUN dotnet restore DeployIt/DeployIt.csproj
COPY DeployIt/ DeployIt/
RUN dotnet publish DeployIt/DeployIt.csproj -c Release --no-restore -o /out /p:UseAppHost=false

FROM build AS tests
USER root
RUN apt-get update && apt-get install -y --no-install-recommends git openssh-client util-linux coreutils \
    && rm -rf /var/lib/apt/lists/*
COPY tests/DeployIt.Tests/ tests/DeployIt.Tests/
RUN dotnet restore tests/DeployIt.Tests/DeployIt.Tests.csproj
ENTRYPOINT ["dotnet", "test", "tests/DeployIt.Tests/DeployIt.Tests.csproj", "--no-restore", "-c", "Release"]

FROM build AS smoke
USER root
COPY tests/DeployIt.Smoke/ tests/DeployIt.Smoke/
RUN dotnet build tests/DeployIt.Smoke/DeployIt.Smoke.csproj -c Release \
    && dotnet run --project tests/DeployIt.Smoke -c Release --no-build -- --install
ENTRYPOINT ["dotnet", "run", "--project", "tests/DeployIt.Smoke", "-c", "Release", "--no-build"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
RUN apt-get update && apt-get install -y --no-install-recommends curl ca-certificates \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /data && chown "$APP_UID:$APP_UID" /data
WORKDIR /app
COPY --from=build --chown=app:app /out .
ENV ASPNETCORE_HTTP_PORTS=8080 DataDirectory=/data
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "DeployIt.dll"]
