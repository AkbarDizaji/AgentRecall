FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props ./
COPY src/AgentRecall.Core/AgentRecall.Core.csproj src/AgentRecall.Core/
COPY src/AgentRecall.Infrastructure/AgentRecall.Infrastructure.csproj src/AgentRecall.Infrastructure/
COPY src/AgentRecall.Cli/AgentRecall.Cli.csproj src/AgentRecall.Cli/
RUN dotnet restore src/AgentRecall.Cli/AgentRecall.Cli.csproj

COPY src/ src/
RUN dotnet publish src/AgentRecall.Cli/AgentRecall.Cli.csproj \
    --framework net10.0 \
    --configuration Release \
    --no-restore \
    --output /app/publish

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "agentrecall.dll", "mcp"]
