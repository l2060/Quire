#!/usr/bin/env bash
# Builds the .NET 10 RID-specific Native AOT tool packages for quire-cli.
#
#   packaging/pack-aot-tool.sh [OUTPUT_DIR]
#
# Produces in OUTPUT_DIR (default: artifacts/aot-tool):
#   quire-cli.<version>.nupkg            pointer package; `dotnet tool install quire-cli` resolves it
#   quire-cli.linux-x64.<version>.nupkg  Native AOT
#   quire-cli.win-x64.<version>.nupkg    Native AOT, only when the Windows link libraries are configured
#   quire-cli.any.<version>.nupkg        framework-dependent fallback for other platforms
#
# Native AOT cannot cross operating systems, so the win-x64 package is linked on Linux with lld-link
# and the Windows SDK/MSVC CRT import libraries:
#   AOT_WIN_SDK_LIB  Windows SDK libs (Microsoft.Windows.SDK.CPP.x64, lowercase aliases)
#   AOT_WIN_CRT_LIB  VC\Tools\MSVC\<version>\lib\x64 (libcmt, libcpmt, oldnames, ...)
# Without both variables only the pointer, linux-x64 and any packages are built.

set -euo pipefail
cd "$(dirname "$0")/.."
output="${1:-artifacts/aot-tool}"

pack() { dotnet pack quire-cli/quire-cli.csproj -c Release -p:GeneratePackageOnBuild=false -o "$output" --nologo "$@"; }

mkdir -p "$output"
rm -f "$output"/quire-cli*.nupkg

pack -p:PublishAot=true
pack -r linux-x64 -p:PublishAot=true
pack -r any -p:PublishAot=false

if [ -n "${AOT_WIN_CRT_LIB:-}" ] && [ -n "${AOT_WIN_SDK_LIB:-}" ]; then
	export AOT_WIN_CRT_LIB AOT_WIN_SDK_LIB
	linker="$(mktemp)"
	cat >"$linker" <<'EOF'
#!/usr/bin/env bash
# Minimal link.exe replacement: sanitize the Native AOT response file and drive lld-link.
set -euo pipefail
rsp="${1#@}"
tmp="$(mktemp)"
sed -e 's|^/SOURCELINK:.*$||' -e 's| /NOEXP||' -e 's|^/DEBUG$||' "$rsp" >"$tmp"
printf '/LIBPATH:%s\n/LIBPATH:%s\n' "$AOT_WIN_CRT_LIB" "$AOT_WIN_SDK_LIB" >>"$tmp"
exec lld-link "@$tmp"
EOF
	chmod +x "$linker"
	pack -r win-x64 -p:PublishAot=true -p:DisableUnsupportedError=true \
		-p:IlcUseEnvironmentalTools=true -p:CppLinker="$linker"
	rm -f "$linker"
else
	echo "Skipping win-x64: set AOT_WIN_CRT_LIB and AOT_WIN_SDK_LIB to cross-link it on Linux." >&2
fi

ls -lh "$output"
