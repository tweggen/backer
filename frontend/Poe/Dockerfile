FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["frontend/Poe/Poe.csproj", "frontend/Poe/"]
COPY ["application/Hannibal/Hannibal.csproj", "application/Hannibal/"]
COPY ["Tools/Tools.csproj", "Tools/"]
COPY . .

RUN dotnet restore "frontend/Poe/Poe.csproj"
WORKDIR "/src/frontend/Poe"
RUN dotnet build "./Poe.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./Poe.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Poe.dll"]
