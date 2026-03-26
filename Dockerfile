FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/NetworkGuard/NetworkGuard.csproj ./
RUN dotnet restore

COPY src/NetworkGuard/ ./
RUN dotnet publish -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# Install iproute2 for "ip neigh" (ARP table on Linux)
RUN apt-get update && apt-get install -y --no-install-recommends iproute2 && rm -rf /var/lib/apt/lists/*

WORKDIR /app

COPY --from=build /app .

RUN mkdir -p /app/blocklists /app/data

EXPOSE 8080
EXPOSE 53/udp

ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

ENTRYPOINT ["dotnet", "NetworkGuard.dll"]
