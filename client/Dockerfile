# syntax=docker/dockerfile:1

# --------------------------------------------------------------------------- build ---------

FROM node:22-alpine AS build
WORKDIR /src

# Lockfile first, for the same caching reason as the .NET build. `npm ci` rather than
# `npm install`: it installs exactly what the lockfile pins and fails if package.json has
# drifted from it.
COPY package.json package-lock.json ./
RUN npm ci

COPY . .

# `tsc -b && vite build`, so the image cannot be produced from code that does not type-check.
RUN npm run build

# -------------------------------------------------------------------------- client ---------

FROM nginx:1.27-alpine AS client

COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /src/dist /usr/share/nginx/html

EXPOSE 80
