FROM mcr.microsoft.com/dotnet/sdk:10.0.400 AS build
WORKDIR /source
COPY global.json Directory.Build.props ./
COPY src/Dse.Domain/ src/Dse.Domain/
COPY src/Dse.Infrastructure/ src/Dse.Infrastructure/
COPY src/Dse.Evaluation/ src/Dse.Evaluation/
COPY src/Dse.ProductionEvaluation/ src/Dse.ProductionEvaluation/
RUN dotnet restore src/Dse.ProductionEvaluation --locked-mode
RUN dotnet publish src/Dse.ProductionEvaluation -c Release --no-restore -o /app
FROM mcr.microsoft.com/dotnet/aspnet:10.0.11
WORKDIR /app
COPY --from=build /app .
COPY evaluation/issue-30/independent/dataset.json data/dataset.json
COPY evaluation/issue-30/independent/approval.json data/approval.json
USER $APP_UID
ENTRYPOINT ["dotnet", "Dse.ProductionEvaluation.dll"]
