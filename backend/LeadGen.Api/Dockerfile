# Build context is ./backend (see docker-compose.yml)
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY LeadGen.Core/LeadGen.Core.csproj LeadGen.Core/
COPY LeadGen.Api/LeadGen.Api.csproj LeadGen.Api/
RUN dotnet restore LeadGen.Api/LeadGen.Api.csproj
COPY LeadGen.Core LeadGen.Core
COPY LeadGen.Api LeadGen.Api
RUN dotnet publish LeadGen.Api/LeadGen.Api.csproj -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /out .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "LeadGen.Api.dll"]
