#!/bin/bash

# dotnet 6 or higher is included in Ubuntu 22.04 and up

# install dev-dependencies
 ##sudo apt-get update; \
 ## sudo apt-get -y install dotnet-sdk-6.0 git cmake ninja-build build-essential libssl-dev pkg-config libboost-all-dev libsodium-dev libzmq5 libgmp-dev

#sudo apt-get -y install dotnet-sdk-6.0 git cmake clang ninja-build build-essential libssl-dev pkg-config libboost-all-dev libsodium-dev libzmq5 libgmp-dev libc++-dev


(cd src/Miningcore && \
BUILDIR=${1:-../../build} && \
echo "Building into $BUILDIR" && \
dotnet publish -c Release --framework net6.0 -o $BUILDIR; \
mkdir -p build && cp -r ./bin/Release/net6.0/* ./build)