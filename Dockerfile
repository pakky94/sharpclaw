FROM node:22-alpine AS web-build
WORKDIR /src/sharpclaw-web

COPY sharpclaw-web/package*.json ./
RUN npm ci

COPY sharpclaw-web/ ./
ARG VITE_API_BASE_URL=
ENV VITE_API_BASE_URL=${VITE_API_BASE_URL}
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS api-build
WORKDIR /src

COPY SharpClaw/ ./SharpClaw/
RUN dotnet restore SharpClaw/SharpClaw.API/SharpClaw.API.csproj
RUN dotnet publish SharpClaw/SharpClaw.API/SharpClaw.API.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

COPY --from=api-build /app/publish ./
COPY --from=web-build /src/sharpclaw-web/dist ./wwwroot

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "SharpClaw.API.dll"]
