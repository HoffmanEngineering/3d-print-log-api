#!/bin/bash
# Generate code coverage report for CI/scripting use
# Outputs: TestResults/CoverageReport/Summary.json (per-class coverage data)
#          TestResults/CoverageReport/SummaryGithub.md (human-readable summary)

set -e

REPO_ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
RESULTS_DIR="$REPO_ROOT/TestResults"

echo "Cleaning previous results..."
rm -rf "$RESULTS_DIR"

echo "Running tests with coverage..."
# See coverage.ps1 for why this is no longer `--collect "XPlat Code Coverage"`: under
# Microsoft.Testing.Platform (#70) coverage is an extension of the test executable, so the options
# go after `--` and RESULTS_DIR must be absolute because the executable resolves it relative to the
# test project rather than to this script.
dotnet test -- \
    --coverage \
    --coverage-output-format cobertura \
    --coverage-output coverage.cobertura.xml \
    --results-directory "$RESULTS_DIR"

# Find the coverage file
COVERAGE_FILE=$(find "$RESULTS_DIR" -name "coverage.cobertura.xml" | head -1)

if [ -z "$COVERAGE_FILE" ]; then
    echo "ERROR: No coverage file found!"
    exit 1
fi

echo "Generating reports..."
dotnet tool run reportgenerator \
    "-reports:$COVERAGE_FILE" \
    "-targetdir:$RESULTS_DIR/CoverageReport" \
    "-reporttypes:JsonSummary;MarkdownSummaryGithub" \
    "-filefilters:-**/Migrations/*" \
    "-verbosity:Warning"

echo "Done. Reports at $RESULTS_DIR/CoverageReport/"
