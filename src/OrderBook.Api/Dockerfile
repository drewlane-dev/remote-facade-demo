FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/OrderBook.Domain/OrderBook.Domain.csproj src/OrderBook.Domain/
COPY src/OrderBook.Api/OrderBook.Api.csproj src/OrderBook.Api/
RUN dotnet restore src/OrderBook.Api/OrderBook.Api.csproj
COPY src/OrderBook.Domain/ src/OrderBook.Domain/
COPY src/OrderBook.Api/ src/OrderBook.Api/
RUN dotnet publish src/OrderBook.Api/OrderBook.Api.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "OrderBook.Api.dll"]
