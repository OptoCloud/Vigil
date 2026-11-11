#!/bin/sh
# Builds vigil-core.tar.gz: vigil-core as one NativeAOT binary, plus the dashboard's static
# build beside it, ready to unpack into /opt/vigil-core on the host.
#
# linux-x64 (glibc), for the Debian guest vigil-core runs in. The binary links only libc and
# libm; OpenSSL is loaded when TLS is first used (ntfy, and Postgres if it asks for it), so the
# guest needs:
#   apt install libssl3 ca-certificates
# No .NET runtime and no libicu: the runtime is compiled in and InvariantGlobalization is on.
#
# NativeAOT compiles to machine code with clang, and it does not cross-compile between operating
# systems (docs/DECISIONS.md ADR-016). On Linux with clang and zlib's headers installed, it builds
# here; anywhere else (Windows, or Linux without clang) it builds inside the .NET SDK image,
# which needs only docker or podman.
set -eu
root="$(cd "$(dirname "$0")/../.." && pwd)"
out="$root/.package/vigil-core"
rm -rf "$root/.package" && mkdir -p "$out"

(cd "$root/web" && npm ci && npm run build)
cp -r "$root/web/build" "$out/web"

# The SDK image by digest, so a package built in it is reproducible: the tag it was taken from
# is mcr.microsoft.com/dotnet/sdk:11.0 (Debian). Bump both together.
sdk_image='mcr.microsoft.com/dotnet/sdk@sha256:65f3c7c6a25a239ab3e6719f2896147e7b68242ff2d8818dd70498bde6ef8bea'
publish='dotnet publish core/Vigil.Core -c Release -r linux-x64 -p:AssemblyName=vigil-core'

if [ "$(uname -s)" = Linux ] && command -v clang >/dev/null 2>&1; then
	(cd "$root" && $publish -o "$out")
else
	engine="$(command -v docker || command -v podman || true)"
	if [ -z "$engine" ]; then
		echo 'NativeAOT needs Linux with clang, or docker or podman to build in the SDK image.' >&2
		exit 1
	fi
	# The tree is copied in rather than built in place, so the host's bin/ and obj/ (another
	# OS's, perhaps) never meet the container's.
	"$engine" run --rm -v "$root:/src:ro" -v "$out:/out" "$sdk_image" sh -c "
		set -e
		apt-get update -qq && DEBIAN_FRONTEND=noninteractive apt-get install -y -qq clang zlib1g-dev >/dev/null
		cp -r /src /work && cd /work && rm -rf core/*/bin core/*/obj
		$publish -o /out
		chown -R $(id -u):$(id -g) /out"
fi

# The symbols are 56 MB and only a debugger wants them; keep them out of the package.
rm -f "$out/vigil-core.dbg"

cp "$root/core/deploy/vigil-core.service" "$root/core/deploy/vigil-core.env" "$out/"
# Modes and ownership set explicitly rather than taken from the filesystem: on Windows there
# are no Unix permission bits to take, and the binary would arrive without its execute bit.
archive="$root/vigil-core.tar"
rm -f "$archive" "$archive.gz"
tar -cf "$archive" --owner=0 --group=0 --numeric-owner --mode='u=rwX,go=rX' 	--exclude=vigil-core/vigil-core -C "$root/.package" vigil-core
tar -rf "$archive" --owner=0 --group=0 --numeric-owner --mode=0755 -C "$root/.package" vigil-core/vigil-core
gzip -9 "$archive"
rm -rf "$root/.package"
echo "wrote $root/vigil-core.tar.gz"
