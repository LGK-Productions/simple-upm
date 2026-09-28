FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
       clang zlib1g-dev

WORKDIR /src

COPY *.cs app.cs

RUN dotnet publish app.cs -o /app

FROM mcr.microsoft.com/dotnet/runtime-deps:11.0 AS final
WORKDIR /app
COPY --from=build /app/app .

WORKDIR /app/packages
ENTRYPOINT ["/app/app"]
