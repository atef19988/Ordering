# Build context is web/order-console:
#   docker build -t ordering-web web/order-console
FROM node:22-alpine AS build
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci
COPY . .
RUN npm run build -- --configuration production

# Static files behind nginx; /api and /health are proxied to the API container,
# mirroring proxy.conf.json for `ng serve`.
FROM nginx:1.27-alpine AS runtime
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist/order-console/browser /usr/share/nginx/html
EXPOSE 80
