FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY src/OpcUaKb.Pipeline/OpcUaKb.Pipeline.csproj src/OpcUaKb.Pipeline/
RUN dotnet restore src/OpcUaKb.Pipeline/OpcUaKb.Pipeline.csproj
COPY src/OpcUaKb.Pipeline/ src/OpcUaKb.Pipeline/
RUN dotnet publish src/OpcUaKb.Pipeline/OpcUaKb.Pipeline.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:9.0
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "OpcUaKb.Pipeline.dll"]
