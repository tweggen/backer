FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
# Switch back to root temporarily to install ping
USER root

# Install ping (from iputils-ping)
RUN apt-get update && apt-get install -y iputils-ping && rm -rf /var/lib/apt/lists/*

RUN mkdir -p /home/app/.aspnet/DataProtection-Keys \
    && chown -R $APP_UID /home/app/.aspnet
    
# Switch back to app user
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
COPY Api/ Api/
COPY application/ application/
COPY Tools/ Tools/

RUN dotnet restore "Api/Api.csproj"
RUN find /src -ls
WORKDIR "/src/Api"
RUN dotnet build "./Api.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish-api
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./Api.csproj" -c $BUILD_CONFIGURATION -o /app/publish/api /p:UseAppHost=false

FROM base AS final-api
WORKDIR /app
COPY --from=publish-api /app/publish/api .
ENTRYPOINT ["dotnet", "Api.dll"]
