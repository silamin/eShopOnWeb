# RUN FROM THE REPO ROOT (folder with the .sln file):
#   docker compose build sql-init
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /app
COPY . .
WORKDIR /app/src/SqlInit
RUN dotnet restore
RUN dotnet publish "./SqlInit.csproj" -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/runtime:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "SqlInit.dll"]
