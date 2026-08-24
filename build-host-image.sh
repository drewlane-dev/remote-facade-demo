#!/bin/sh
# One-time prerequisite, and ONLY until v1.1.0 is published.
#
# The demo references ghcr.io/drewlane-dev/remote-class-host:1.1.0 the way any
# consumer would. That tag does not exist on the registry yet, so this builds it
# locally under the same name. Docker finds the local image and never reaches
# for the registry.
#
# When v1.1.0 ships, delete this script: the demo is unchanged and the image is
# simply pulled.
set -eu

TAG="ghcr.io/drewlane-dev/remote-class-host:1.1.0"
SRC="${1:-../remote-class-host}"

if [ ! -f "${SRC}/Dockerfile" ]; then
  echo "No Dockerfile at ${SRC}." >&2
  echo "Pass the path to a remote-class-host checkout: ./build-host-image.sh <path>" >&2
  exit 1
fi

echo "Building ${TAG} from ${SRC} ..."
docker build -t "${TAG}" "${SRC}"
echo
echo "Done. Now run:  dotnet run --project tests/OrderBook.Tests"
