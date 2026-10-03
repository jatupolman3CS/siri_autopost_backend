# Build from the repository root:
#   docker build -f backend/Dockerfile -t fbap-backend .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY backend/SIRI.AUTOPOST.Server/SIRI.AUTOPOST.Server.csproj backend/SIRI.AUTOPOST.Server/
RUN dotnet restore backend/SIRI.AUTOPOST.Server/SIRI.AUTOPOST.Server.csproj

COPY client/dashboard.html client/dashboard.css client/dashboard.js ./client/
COPY client/lib ./client/lib
COPY client/icons ./client/icons
COPY backend/SIRI.AUTOPOST.Server ./backend/SIRI.AUTOPOST.Server

RUN dotnet publish backend/SIRI.AUTOPOST.Server/SIRI.AUTOPOST.Server.csproj -c Release -o /out --no-restore

# Copy client extension files to /out/extension so ExtensionPath finds them anywhere
RUN mkdir -p /out/extension && \
    cp /src/client/dashboard.html /out/extension/ && \
    cp /src/client/dashboard.css /out/extension/ && \
    cp /src/client/dashboard.js /out/extension/ && \
    cp -r /src/client/lib /out/extension/lib && \
    cp -r /src/client/icons /out/extension/icons

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out .
ENV ASPNETCORE_URLS=http://+:8080 \
    DataProtection__KeysPath=/data/keys
VOLUME /data
EXPOSE 8080
ENTRYPOINT ["dotnet", "SIRI.AUTOPOST.Server.dll"]
