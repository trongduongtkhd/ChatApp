FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/BuildingBlocks/ChatApp.Contracts/ src/BuildingBlocks/ChatApp.Contracts/
COPY src/Services/Presence/ChatApp.PresenceService/ src/Services/Presence/ChatApp.PresenceService/
RUN dotnet publish src/Services/Presence/ChatApp.PresenceService/ChatApp.PresenceService.csproj -c Release -o /app/publish
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .
USER $APP_UID
EXPOSE 5005 5015
ENTRYPOINT ["dotnet", "ChatApp.PresenceService.dll"]
