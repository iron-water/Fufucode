# Build on the same Linux release as the daemon to match its glibc baseline.
FROM node:22-bookworm AS node
FROM ubuntu:26.04
COPY --from=node /usr/local/ /usr/local/
RUN apt-get update && apt-get install -y --no-install-recommends fuse3 libsqlite3-0 libstdc++6 git python3 make g++ ca-certificates && rm -rf /var/lib/apt/lists/*
WORKDIR /tmp
