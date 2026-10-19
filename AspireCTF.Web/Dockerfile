# 构建上下文为仓库根目录： podman build -f AspireCTF.Web/Dockerfile .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY AspireCTF.Contracts/AspireCTF.Contracts.csproj AspireCTF.Contracts/
COPY AspireCTF.ServiceDefaults/AspireCTF.ServiceDefaults.csproj AspireCTF.ServiceDefaults/
COPY AspireCTF.Web/AspireCTF.Web.csproj AspireCTF.Web/
RUN dotnet restore AspireCTF.Web/AspireCTF.Web.csproj

COPY AspireCTF.Contracts/ AspireCTF.Contracts/
COPY AspireCTF.ServiceDefaults/ AspireCTF.ServiceDefaults/
COPY AspireCTF.Web/ AspireCTF.Web/
RUN dotnet publish AspireCTF.Web/AspireCTF.Web.csproj -c Release -o /app/publish /p:UseAppHost=false /p:DebugType=none

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_EnableDiagnostics=0

EXPOSE 8080
ENTRYPOINT ["dotnet", "AspireCTF.Web.dll"]
