FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS sdk
WORKDIR /source
COPY src/CSweet.Hosting/CSweet.Hosting.csproj src/CSweet.Hosting/
RUN dotnet restore src/CSweet.Hosting/CSweet.Hosting.csproj
COPY src/ src/
COPY linux/ linux/
COPY deploy/create-seed.sh deploy/create-seed.sh
RUN dotnet publish src/CSweet.Hosting/CSweet.Hosting.csproj --no-restore -c Release -o /out

FROM sdk AS test
RUN apt-get update && apt-get install -y --no-install-recommends genisoimage && rm -rf /var/lib/apt/lists/*
COPY tests/ tests/
RUN dotnet test tests/CSweet.Hosting.Tests/CSweet.Hosting.Tests.csproj -c Release

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble
RUN apt-get update && apt-get install -y --no-install-recommends genisoimage curl skopeo && rm -rf /var/lib/apt/lists/* \
    && useradd --uid 10001 --no-create-home hosting && install -d -m 0700 -o hosting -g hosting /state
WORKDIR /app
COPY --from=sdk /out/ ./
ENV ASPNETCORE_HTTP_PORTS=8080 DOTNET_EnableDiagnostics=0
USER 10001
ENTRYPOINT ["dotnet", "CSweet.Hosting.dll"]
