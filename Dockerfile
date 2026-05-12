FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS build

# install refasmer CLI
RUN dotnet tool install -g JetBrains.Refasmer.CliTool

# build csstubgen
WORKDIR /build
COPY src/ ./
RUN dotnet restore
RUN dotnet publish -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine
COPY --from=build /root/.dotnet/tools /root/.dotnet/tools
COPY --from=build /app /app
ENV PATH="$PATH:/root/.dotnet/tools"

COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh

WORKDIR /work
ENTRYPOINT ["/entrypoint.sh"]
