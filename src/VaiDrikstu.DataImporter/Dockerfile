FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/VaiDrikstu.DataImporter/VaiDrikstu.DataImporter.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app/publish .
COPY data/sources ./data/sources
ENV DATA_SOURCES_PATH=/app/data/sources
ENTRYPOINT ["dotnet", "VaiDrikstu.DataImporter.dll"]
