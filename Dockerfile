FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY NovaWallet.API.csproj ./
COPY NovaWallet.Domain/NovaWallet.Domain.csproj NovaWallet.Domain/
COPY NovaWallet.Repositories/NovaWallet.Repositories.csproj NovaWallet.Repositories/
COPY NovaWallet.Service/NovaWallet.Service.csproj NovaWallet.Service/
RUN dotnet restore NovaWallet.API.csproj
COPY . .
RUN dotnet publish NovaWallet.API.csproj -c Release --no-restore -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "NovaWallet.API.dll"]
