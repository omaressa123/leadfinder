#!/usr/bin/env bash
# Runs the LeadGen.Core test suite offline (no Python AI service required).
# Needs: the .NET SDK.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
dotnet run --project "$ROOT/backend/LeadGen.Core.Tests" -- "${1:-all}"
