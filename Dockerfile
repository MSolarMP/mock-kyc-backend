# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Kyc.sln ./
COPY src/Kyc.Domain/Kyc.Domain.csproj src/Kyc.Domain/
COPY src/Kyc.Infrastructure/Kyc.Infrastructure.csproj src/Kyc.Infrastructure/
COPY src/Kyc.Api/Kyc.Api.csproj src/Kyc.Api/
RUN dotnet restore src/Kyc.Api/Kyc.Api.csproj
COPY src/ src/
RUN dotnet publish src/Kyc.Api/Kyc.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
# non-root user from the .NET base image
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Kyc.Api.dll"]
