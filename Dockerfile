# One image definition for all three services: docker build --build-arg PROJECT=TranslateFlow.Api .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG PROJECT
WORKDIR /src
COPY . .
RUN dotnet publish src/${PROJECT}/${PROJECT}.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
ARG PROJECT
ENV APP_DLL=${PROJECT}.dll
WORKDIR /app
COPY --from=build /app .
RUN mkdir -p /data && chown $APP_UID /data
USER $APP_UID
ENTRYPOINT ["sh", "-c", "exec dotnet $APP_DLL"]
