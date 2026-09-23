# Bygg fra starter-kit-mappen:  az acr build -r <acr> -t mini-nils/<label>/receiver:<tag> -f src/Receiver/Dockerfile .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Receiver/Receiver.csproj src/Receiver/
RUN dotnet restore src/Receiver/Receiver.csproj
COPY src/Receiver/ src/Receiver/
RUN dotnet publish src/Receiver/Receiver.csproj -c Release -o /out --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Receiver.dll"]
