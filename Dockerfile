# ---- Build stage ----
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# copy csproj and restore
COPY FileAPI.csproj ./
RUN dotnet restore

# copy the rest
COPY . ./
RUN dotnet publish -c Release -o /out /p:UseAppHost=false

# ---- Runtime stage ----
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

# Cloud Run/AE expect the service to listen on PORT env (default 8080)
ENV ASPNETCORE_URLS=http://0.0.0.0:${PORT:-8080}
# (opcjonalnie) Minimalne logowanie
ENV Logging__LogLevel__Default=Information
ENV Logging__LogLevel__Microsoft.AspNetCore=Warning

COPY --from=build /out ./
EXPOSE 8080
ENTRYPOINT ["dotnet", "FileAPI.dll"]
