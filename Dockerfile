# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /source

# Copy solution and project files
COPY Conduit.sln .
COPY src/Conduit.Web/*.csproj ./src/Conduit.Web/
COPY src/Conduit.Core/*.csproj ./src/Conduit.Core/
COPY src/Conduit.DataAccess/*.csproj ./src/Conduit.DataAccess/

# Restore packages
RUN dotnet restore

# Copy source code
COPY src/ ./src/
COPY Database/ ./Database/

# Build and publish
WORKDIR /source/src/Conduit.Web
RUN dotnet publish -c Release -o /app

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app

# Install SQL Server tools for database initialization
RUN apt-get update && apt-get install -y \
    curl \
    gnupg \
    && curl https://packages.microsoft.com/keys/microsoft.asc | apt-key add - \
    && curl https://packages.microsoft.com/config/ubuntu/20.04/prod.list > /etc/apt/sources.list.d/mssql-release.list \
    && apt-get update \
    && ACCEPT_EULA=Y apt-get install -y msodbcsql17 \
    && apt-get clean \
    && rm -rf /var/lib/apt/lists/*

# Copy published app
COPY --from=build /app .

# Create directories
RUN mkdir -p /app/logs /app/config

# Expose ports
EXPOSE 80
EXPOSE 443

# Health check
HEALTHCHECK --interval=30s --timeout=10s --start-period=40s --retries=3 \
    CMD curl -f http://localhost/health || exit 1

# Set environment variables
ENV ASPNETCORE_URLS=http://+:80
ENV ASPNETCORE_ENVIRONMENT=Production

# Entry point
ENTRYPOINT ["dotnet", "Conduit.Web.dll"]