FROM node:26-alpine AS build
WORKDIR /app
COPY web/package.json web/pnpm-lock.yaml* web/pnpm-workspace.yaml ./
RUN npm install -g pnpm@11.7.0 && pnpm install --frozen-lockfile=false
COPY web .
ARG VITE_API_URL=http://localhost:8080
ENV VITE_API_URL=$VITE_API_URL
RUN pnpm build

FROM nginx:1.27-alpine
COPY --from=build /app/dist /usr/share/nginx/html
EXPOSE 80
