# SIRIAUTOPOST.Api. Build from the repository root:
#   docker build -f src/SIRIAUTOPOST.Api/Dockerfile -t siriautopost-api .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first so the package layer is cached between code changes.
COPY src/SIRIAUTOPOST.Domain/SIRIAUTOPOST.Domain.csproj src/SIRIAUTOPOST.Domain/
COPY src/SIRIAUTOPOST.Application/SIRIAUTOPOST.Application.csproj src/SIRIAUTOPOST.Application/
COPY src/SIRIAUTOPOST.Infrastructure/SIRIAUTOPOST.Infrastructure.csproj src/SIRIAUTOPOST.Infrastructure/
COPY src/SIRIAUTOPOST.Api/SIRIAUTOPOST.Api.csproj src/SIRIAUTOPOST.Api/
RUN dotnet restore src/SIRIAUTOPOST.Api/SIRIAUTOPOST.Api.csproj

COPY src ./src
RUN dotnet publish src/SIRIAUTOPOST.Api/SIRIAUTOPOST.Api.csproj -c Release -o /out --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "SIRIAUTOPOST.Api.dll"]
