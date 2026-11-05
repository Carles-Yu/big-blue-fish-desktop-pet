FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Version.props Version.props
COPY code/Directory.Build.props code/Directory.Build.props
COPY code/BigBlueFish.Memory/ code/BigBlueFish.Memory/
COPY server/BigBlueFish.SyncServer/ server/BigBlueFish.SyncServer/
RUN dotnet publish server/BigBlueFish.SyncServer/BigBlueFish.SyncServer.csproj -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "BigBlueFish.SyncServer.dll"]
