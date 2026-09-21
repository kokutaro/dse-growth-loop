FROM node:24.13.0-bookworm-slim AS web
WORKDIR /web
COPY web/package*.json ./
RUN npm ci
COPY web/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0.400 AS build
WORKDIR /source
COPY global.json Directory.Build.props ./
COPY src/Dse.Domain/ src/Dse.Domain/
COPY src/Dse.Infrastructure/ src/Dse.Infrastructure/
COPY src/Dse.Api/ src/Dse.Api/
RUN dotnet restore src/Dse.Api --locked-mode
RUN dotnet publish src/Dse.Api -c Release --no-restore -o /app
COPY --from=web /web/dist /app/wwwroot

FROM mcr.microsoft.com/dotnet/aspnet:10.0.11 AS runtime
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Dse.Api.dll"]
