# Context is the app folder's ./build, so ./config (the API key) is never sent to the builder.
FROM mcr.microsoft.com/dotnet/sdk:10.0.404 AS build
WORKDIR /src
COPY . .
RUN sha256sum -c SHA256SUMS
RUN mkdir -p openapi \
 && curl -fsSL -o openapi/unifi-network-11.0.97.json https://developer.ui.com/network/v11.0.97/openapi.json \
 && echo "0828aa9f017cce92cd035429c7e19c933d0b3387c561aac17c468b4c2ca8be66  openapi/unifi-network-11.0.97.json" | sha256sum -c -
RUN dotnet publish src/UnifiMcp/UnifiMcp.csproj -c Release -o /app && chmod -R a+rX /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0.15
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "unifi-mcp.dll"]
