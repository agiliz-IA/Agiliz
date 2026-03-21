FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["Agiliz.Core/Agiliz.Core.csproj",      "Agiliz.Core/"]
COPY ["Agiliz.Wizard/Agiliz.Wizard.csproj",  "Agiliz.Wizard/"]
RUN dotnet restore "Agiliz.Wizard/Agiliz.Wizard.csproj"

COPY Agiliz.Core/   Agiliz.Core/
COPY Agiliz.Wizard/ Agiliz.Wizard/
RUN dotnet publish "Agiliz.Wizard/Agiliz.Wizard.csproj" -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

EXPOSE 8080
ENTRYPOINT ["dotnet", "Agiliz.Wizard.dll"]
