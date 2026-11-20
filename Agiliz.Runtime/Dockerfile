FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["Agiliz.Core/Agiliz.Core.csproj",       "Agiliz.Core/"]
COPY ["Agiliz.Runtime/Agiliz.Runtime.csproj",  "Agiliz.Runtime/"]
RUN dotnet restore "Agiliz.Runtime/Agiliz.Runtime.csproj"

COPY Agiliz.Core/    Agiliz.Core/
COPY Agiliz.Runtime/ Agiliz.Runtime/
RUN dotnet publish "Agiliz.Runtime/Agiliz.Runtime.csproj" -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

EXPOSE 8080
ENTRYPOINT ["dotnet", "Agiliz.Runtime.dll"]
