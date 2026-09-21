FROM mcr.microsoft.com/dotnet/sdk:10.0.400 AS build
WORKDIR /source
COPY global.json Directory.Build.props ./
COPY src/Dse.Domain/ src/Dse.Domain/
COPY src/Dse.Infrastructure/ src/Dse.Infrastructure/
COPY src/Dse.Worker/ src/Dse.Worker/
RUN dotnet restore src/Dse.Worker --locked-mode
RUN dotnet publish src/Dse.Worker -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0.11 AS runtime
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "Dse.Worker.dll"]
