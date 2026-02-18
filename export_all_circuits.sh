#!/bin/bash
# export_all_circuits.sh - Batch export all LiveSPICE example circuits to C

set -e

echo "LiveSPICE - Export All Circuits"
echo "==============================="
echo ""

# Configuration
EXAMPLES_DIR="Tests/Examples"
EXPORT_DIR="exported_circuits"
COMPILE_DYLIB=true

# Colors for output
GREEN='\033[0;32m'
RED='\033[0;31m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

# Create export directory
mkdir -p "$EXPORT_DIR"

# Check if export tool exists
if [ ! -d "ExportToC" ]; then
    echo -e "${RED}Error: ExportToC directory not found${NC}"
    exit 1
fi

echo "Building export tool..."
cd ExportToC
dotnet build -c Release -v quiet
cd ..
echo -e "${GREEN}✓ Export tool ready${NC}"
echo ""

# Counter for statistics
TOTAL=0
SUCCESS=0
FAILED=0

# Function to export a single circuit
export_circuit() {
    local schx_file="$1"
    local filename=$(basename "$schx_file")
    local name="${filename%.schx}"
    local safe_name=$(echo "$name" | tr ' ' '_' | tr -d '+')
    local c_file="$EXPORT_DIR/${safe_name}.c"
    local dylib_file="$EXPORT_DIR/${safe_name}.dylib"
    
    echo "Exporting: $name"
    
    if dotnet run --project ExportToC -- \
        --input "$schx_file" \
        --output "$c_file" \
        --sample-rate 48000 \
        --buffer-size 256 \
        --oversample 8 2>/dev/null; then
        
        if [ -f "$c_file" ]; then
            local lines=$(wc -l < "$c_file")
            echo -e "  ${GREEN}✓${NC} C code: $lines lines"
            
            # Compile to dylib if requested
            if [ "$COMPILE_DYLIB" = true ]; then
                if clang -dynamiclib -arch x86_64 -arch arm64 \
                    -o "$dylib_file" \
                    "$c_file" \
                    -O2 -lm 2>/dev/null; then
                    echo -e "  ${GREEN}✓${NC} Dylib: $(du -h "$dylib_file" | cut -f1)"
                    return 0
                else
                    echo -e "  ${YELLOW}⚠${NC} Dylib compilation failed"
                    return 1
                fi
            fi
            return 0
        else
            echo -e "  ${RED}✗${NC} Export failed - no output file"
            return 1
        fi
    else
        echo -e "  ${RED}✗${NC} Export failed"
        return 1
    fi
}

echo "Scanning for circuits in $EXAMPLES_DIR..."
echo ""

# Count total circuits
TOTAL=$(find "$EXAMPLES_DIR" -name "*.schx" | wc -l)
echo "Found $TOTAL circuits to export"
echo ""

# Export each circuit
find "$EXAMPLES_DIR" -name "*.schx" | sort | while read -r schx_file; do
    if export_circuit "$schx_file"; then
        ((SUCCESS++))
    else
        ((FAILED++))
    fi
    echo ""
done

# Summary
echo "==============================="
echo "Export Complete!"
echo "==============================="
echo ""
echo "Statistics:"
echo "  Total circuits:  $TOTAL"
echo -e "  ${GREEN}Successful${NC}:      $SUCCESS"
if [ $FAILED -gt 0 ]; then
    echo -e "  ${RED}Failed${NC}:          $FAILED"
fi
echo ""

echo "Output directory: $EXPORT_DIR/"
echo ""
echo "Generated files:"
ls -lh "$EXPORT_DIR/" | tail -n +2
echo ""

echo "Next steps:"
echo "  1. Test individual circuits:"
echo "     ./circuit_test -i test.wav -c $EXPORT_DIR/Marshall_Blues_Breaker.dylib -o out.wav"
echo ""
echo "  2. Bundle dylibs in your app:"
echo "     - macOS: Copy *.dylib to app bundle"
echo "     - iOS: Copy *.dylib to app bundle, sign with codesign"
echo ""
echo "  3. Load in Swift:"
echo "     let handle = dlopen(\"Marshall_Blues_Breaker.dylib\", RTLD_LAZY)"
echo ""

# Create a manifest
echo "Creating manifest..."
MANIFEST="$EXPORT_DIR/manifest.txt"
echo "LiveSPICE Exported Circuits" > "$MANIFEST"
echo "Generated: $(date)" >> "$MANIFEST"
echo "" >> "$MANIFEST"
echo "Circuits:" >> "$MANIFEST"
find "$EXPORT_DIR" -name "*.dylib" | sort | while read -r dylib; do
    name=$(basename "$dylib" .dylib | tr '_' ' ')
    size=$(du -h "$dylib" | cut -f1)
    echo "  - $name ($size)" >> "$MANIFEST"
done
echo -e "${GREEN}✓${NC} Manifest: $MANIFEST"
echo ""

echo -e "${GREEN}All circuits exported!${NC}"