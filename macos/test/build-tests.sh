#!/bin/bash
# Сборка тестовых стендов (снимки экранов и проверка набора), в поставку не входят.
set -euo pipefail
cd "$(dirname "$0")/.."
SDK=$(xcrun --show-sdk-path --sdk macosx)
LIB=$(ls Sources/*.swift | grep -v main.swift)
xcrun swiftc -D SNAPSHOT -sdk "$SDK" -target arm64-apple-macos12.0 -module-name L2TPConnect -o build/snapshot $LIB test/main.swift
echo TESTS BUILT
xcrun swiftc -sdk "$SDK" -target arm64-apple-macos12.0 -module-name L2TPConnect -o build/profiles $LIB test/profiles/main.swift
echo PROFILE TESTS BUILT
xcrun swiftc -sdk "$SDK" -target arm64-apple-macos12.0 -module-name L2TPConnect -o build/probe $LIB test/probe/main.swift
echo PROBE TOOL BUILT
xcrun swiftc -sdk "$SDK" -target arm64-apple-macos12.0 -module-name L2TPConnect -o build/creds $LIB test/creds/main.swift
echo CREDS TEST BUILT
xcrun swiftc -sdk "$SDK" -target arm64-apple-macos12.0 -module-name L2TPConnect -o build/rdphandler $LIB test/rdphandler/main.swift
echo RDP HANDLER TEST BUILT
xcrun swiftc -sdk "$SDK" -target arm64-apple-macos12.0 -module-name L2TPConnect -o build/net $LIB test/net/main.swift
echo NET TEST BUILT
xcrun swiftc -D SNAPSHOT -sdk "$SDK" -target arm64-apple-macos12.0 -module-name L2TPConnect -o build/balance $LIB test/balance/main.swift
echo BALANCE TEST BUILT
xcrun swiftc -sdk "$SDK" -target arm64-apple-macos12.0 -module-name L2TPConnect -o build/dnsconfig $LIB test/dnsconfig/main.swift
echo DNS CONFIG TEST BUILT
xcrun swiftc -sdk "$SDK" -target arm64-apple-macos12.0 -module-name L2TPConnect -o build/rdpfile $LIB test/rdpfile/main.swift
echo RDP FILE TEST BUILT
xcrun swiftc -D SNAPSHOT -sdk "$SDK" -target arm64-apple-macos12.0 -module-name L2TPConnect -o build/update $LIB test/update/main.swift
echo UPDATE TEST BUILT
