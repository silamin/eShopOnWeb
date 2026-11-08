# RUN FROM THE REPO ROOT (folder with the .sln file):
#   docker compose build event-probe
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /app
COPY . .
WORKDIR /app/src/EventProbe
RUN dotnet restore
RUN dotnet publish "./EventProbe.csproj" -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/runtime:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "EventProbe.dll"]
