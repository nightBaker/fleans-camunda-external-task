# Builds the ready-to-run plugin host image.
#   docker build -t fleans-camunda-external-task .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/Fleans.CamundaExternalTask.Host/Fleans.CamundaExternalTask.Host.csproj \
    -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Fleans.CamundaExternalTask.Host.dll"]
