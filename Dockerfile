FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Chalk.Api/Chalk.Api.csproj src/Chalk.Api/
RUN dotnet restore src/Chalk.Api/Chalk.Api.csproj
COPY src/ src/
# Git sha of the build; surfaces as the ?v= cache-buster on UI assets.
ARG CHALK_VERSION=dev
RUN dotnet publish src/Chalk.Api/Chalk.Api.csproj -c Release -o /app/publish /p:UseAppHost=false /p:ChalkVersion=$CHALK_VERSION

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
COPY --from=build /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "Chalk.Api.dll"]
