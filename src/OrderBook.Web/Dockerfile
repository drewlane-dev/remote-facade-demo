# Built by the test fixture at run time, so the demo needs no registry and no
# published image of its own.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/OrderBook.Web/OrderBook.Web.csproj src/OrderBook.Web/
RUN dotnet restore src/OrderBook.Web/OrderBook.Web.csproj
COPY src/OrderBook.Web/ src/OrderBook.Web/
RUN dotnet publish src/OrderBook.Web/OrderBook.Web.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "OrderBook.Web.dll"]
