# RUN FROM THE REPO ROOT (folder with the .sln file):
#   docker compose build catalogservice
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /app
COPY . .
WORKDIR /app/src/CatalogService
RUN dotnet restore
RUN dotnet publish "./CatalogService.csproj" -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "CatalogService.dll"]
