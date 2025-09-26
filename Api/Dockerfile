FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["Api/Api.csproj", "Api/"]
COPY ["application/Hannibal/Hannibal.csproj", "application/Hannibal/"]
COPY ["Tools/Tools.csproj", "Tools/"]
COPY ["Api","application","Tools","./"]

RUN dotnet restore "Api/Api.csproj"
RUN find -ls /src
WORKDIR "/src/Api"
RUN dotnet build "./Api.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish-api
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./Api.csproj" -c $BUILD_CONFIGURATION -o /app/publish/api /p:UseAppHost=false

FROM base AS final-api
WORKDIR /app
COPY --from=publish-api /app/publish/api .
ENTRYPOINT ["dotnet", "Api.dll"]
