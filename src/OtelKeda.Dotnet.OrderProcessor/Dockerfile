FROM mcr.microsoft.com/dotnet/runtime:7.0 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:7.0 AS build
WORKDIR /src
COPY ["OtelKeda.Dotnet.OrderProcessor/OtelKeda.Dotnet.OrderProcessor.csproj", "OtelKeda.Dotnet.OrderProcessor/"]
COPY ["OtelKeda.Dotnet.Contracts/OtelKeda.Dotnet.Contracts.csproj", "OtelKeda.Dotnet.Contracts/"]
RUN dotnet restore "OtelKeda.Dotnet.OrderProcessor/OtelKeda.Dotnet.OrderProcessor.csproj"
COPY . .
WORKDIR "/src/OtelKeda.Dotnet.OrderProcessor"
RUN dotnet build "OtelKeda.Dotnet.OrderProcessor.csproj" -c Release -o /app

FROM build AS publish
RUN dotnet publish "OtelKeda.Dotnet.OrderProcessor.csproj" -c Release -o /app

FROM base AS final
WORKDIR /app
COPY --from=publish /app .
ENTRYPOINT ["dotnet", "OtelKeda.Dotnet.OrderProcessor.dll"]