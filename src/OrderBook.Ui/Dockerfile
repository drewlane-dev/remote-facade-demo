FROM node:22-alpine AS build
WORKDIR /src
COPY src/OrderBook.Ui/package*.json ./
RUN npm ci
COPY src/OrderBook.Ui/ ./
RUN npm run build

FROM nginx:alpine
COPY src/OrderBook.Ui/nginx.conf /etc/nginx/conf.d/default.conf
# Angular's build output lands under dist/<project>/browser.
COPY --from=build /src/dist/orderbook-ui/browser /usr/share/nginx/html
EXPOSE 8080
