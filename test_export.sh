#!/bin/bash
# test_export.sh - Test the export to C workflow

set -e

echo "LiveSPICE Export to C - Test Workflow"
echo "======================================"
echo ""

# Configuration
CIRCUIT_NAME="Marshall Blues Breaker"
SCHX_FILE="Tests/Examples/Marshall Blues Breaker.schx"
EXPORT_DIR="exported_circuits"
TEST_AUDIO="test_sine.wav"
OUTPUT_AUDIO="test_output.wav"

# Colors for output
GREEN='\033[0;32m'
RED='\033[0;31m'
NC='\033[0m' # No Color

echo "Step 1: Building Export Tool"
echo "----------------------------"
cd ExportToC
dotnet build -c Release
cd ..
echo -e "${GREEN}✓ Export tool built${NC}"
echo ""

echo "Step 2: Exporting Circuit to C"
echo "------------------------------"
mkdir -p $EXPORT_DIR

dotnet run --project ExportToC -- \
  --input "$SCHX_FILE" \
  --output "$EXPORT_DIR/blues_breaker.c" \
  --sample-rate 48000 \
  --buffer-size 256 \
  --oversample 8

if [ -f "$EXPORT_DIR/blues_breaker.c" ]; then
    echo -e "${GREEN}✓ C code exported: $EXPORT_DIR/blues_breaker.c${NC}"
    echo "  Lines of code: $(wc -l < $EXPORT_DIR/blues_breaker.c)"
else
    echo -e "${RED}✗ Export failed${NC}"
    exit 1
fi
echo ""

echo "Step 3: Compiling to Dylib"
echo "--------------------------"
# Compile for macOS (universal binary)
clang -dynamiclib -arch x86_64 -arch arm64 \
  -o "$EXPORT_DIR/blues_breaker.dylib" \
  "$EXPORT_DIR/blues_breaker.c" \
  -O2 -lm

if [ -f "$EXPORT_DIR/blues_breaker.dylib" ]; then
    echo -e "${GREEN}✓ Dylib created: $EXPORT_DIR/blues_breaker.dylib${NC}"
    file "$EXPORT_DIR/blues_breaker.dylib"
else
    echo -e "${RED}✗ Compilation failed${NC}"
    exit 1
fi
echo ""

echo "Step 4: Creating Test Audio"
echo "---------------------------"
if command -v sox >/dev/null 2>&1; then
    sox -n -r 48000 -b 16 $TEST_AUDIO synth 2 sine 440
    echo -e "${GREEN}✓ Test audio created: $TEST_AUDIO${NC}"
else
    echo -e "${RED}✗ sox not found. Please install sox:${NC}"
    echo "  brew install sox"
    exit 1
fi
echo ""

echo "Step 5: Testing Circuit with Audio"
echo "----------------------------------"
if [ -f "circuit_test" ]; then
    ./circuit_test \
      --input $TEST_AUDIO \
      --circuit $EXPORT_DIR/blues_breaker.dylib \
      --output $OUTPUT_AUDIO \
      --buffer-size 256 \
      --oversample 8 \
      --measure-latency \
      --verbose
    
    if [ -f "$OUTPUT_AUDIO" ]; then
        echo -e "${GREEN}✓ Audio processed: $OUTPUT_AUDIO${NC}"
    else
        echo -e "${RED}✗ Processing failed${NC}"
        exit 1
    fi
else
    echo -e "${RED}✗ circuit_test not found. Build it first:${NC}"
    echo "  make"
    exit 1
fi
echo ""

echo "Step 6: Verification"
echo "--------------------"
echo "Input file:  $TEST_AUDIO"
echo "Output file: $OUTPUT_AUDIO"
echo ""
echo "File info:"
ls -lh $TEST_AUDIO $OUTPUT_AUDIO
echo ""

echo -e "${GREEN}✓ Test workflow complete!${NC}"
echo ""
echo "Next steps:"
echo "  1. Listen to $OUTPUT_AUDIO to verify the effect"
echo "  2. Try different circuits:"
echo "     - Tests/Examples/Ibanez Tube Screamer TS-9.schx"
echo "     - Tests/Examples/Big Muff Pi.schx"
echo "     - Tests/Examples/Boss Super Overdrive SD-1.schx"
echo "  3. Integrate the dylib into your Swift app"
echo ""

# Cleanup option
read -p "Clean up test files? (y/n) " -n 1 -r
echo
if [[ $REPLY =~ ^[Yy]$ ]]; then
    rm -f $TEST_AUDIO $OUTPUT_AUDIO
    echo "Test files removed"
fi