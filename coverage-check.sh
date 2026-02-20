#!/bin/bash
# Generate code coverage report for CI/scripting use
# Outputs: TestResults/CoverageReport/Summary.json (per-class coverage data)
#          TestResults/CoverageReport/SummaryGithub.md (human-readable summary)

set -e

echo "Cleaning previous results..."
rm -rf TestResults

echo "Running tests with coverage..."
dotnet test --collect:"XPlat Code Coverage" --results-directory TestResults --verbosity quiet

# Find the coverage file
COVERAGE_FILE=$(find TestResults -name "coverage.cobertura.xml" | head -1)

if [ -z "$COVERAGE_FILE" ]; then
    echo "ERROR: No coverage file found!"
    exit 1
fi

echo "Generating reports..."
dotnet tool run reportgenerator \
    "-reports:$COVERAGE_FILE" \
    "-targetdir:TestResults/CoverageReport" \
    "-reporttypes:JsonSummary;MarkdownSummaryGithub" \
    "-filefilters:-**/Migrations/*" \
    "-verbosity:Warning"

echo "Done. Reports at TestResults/CoverageReport/"
