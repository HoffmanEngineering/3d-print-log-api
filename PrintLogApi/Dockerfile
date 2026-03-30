FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 80
EXPOSE 443

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["PrintLogApi/PrintLogApi.csproj", "PrintLogApi/"]
RUN dotnet restore "PrintLogApi/PrintLogApi.csproj"
COPY . .
WORKDIR "/src/PrintLogApi"
RUN dotnet build "PrintLogApi.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "PrintLogApi.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "PrintLogApi.dll"]
