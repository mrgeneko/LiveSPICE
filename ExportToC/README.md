# Export to C Tool

Converts LiveSPICE `.schx` circuit files to C code that can be compiled to native dylibs for macOS/iOS.

## Overview

This tool bridges the gap between LiveSPICE's symbolic circuit analysis and native C code:

```
.schx file → [LiveSPICE Analysis] → C Code → [clang] → .dylib → [Swift App]
```

## Building

```bash
cd ExportToC
dotnet build
cd ..
```

## Usage

### Basic Export

```bash
dotnet run --project ExportToC -- \
  --input Tests/Examples/Marshall\ JCM800\ 2203\ Preamp.schx \
  --output marshall_preamp.c
```

### Export and Compile

```bash
dotnet run --project ExportToC -- \
  --input Tests/Examples/Marshall\ Blues\ Breaker.schx \
  --output blues_breaker.c \
  --dylib
```

### Options

- `--input FILE` - Input .schx file (required)
- `--output FILE` - Output C file (default: circuit.c)
- `--dylib` - Automatically compile to dylib (requires clang)
- `--sample-rate RATE` - Sample rate in Hz (default: 48000)
- `--buffer-size SIZE` - Buffer size in samples (default: 256)
- `--oversample N` - Oversampling factor (default: 8)

## Example: Marshall Blues Breaker

```bash
# Export the Blues Breaker circuit
dotnet run --project ExportToC -- \
  --input Tests/Examples/Marshall\ Blues\ Breaker.schx \
  --output blues_breaker.c \
  --dylib

# Test it with audio
./circuit_test \
  --input guitar.wav \
  --circuit blues_breaker.dylib \
  --output blues_guitar.wav \
  --measure-latency
```

## How It Works

1. **Load** - Parses the `.schx` XML file
2. **Build** - Constructs the circuit graph (components, nodes, connections)
3. **Analyze** - Runs symbolic analysis to generate equations
4. **Export** - Generates C code with:
   - State variable declarations
   - Initialization function
   - Audio processing function
   - Parameter control functions
   - Cleanup function

## Generated C Code Structure

```c
// Context structure
typedef struct {
    double* state;
    double* parameters;
    // ...
} CircuitContext;

// Initialize circuit
CircuitContext* circuit_init(int sample_rate, int buffer_size, int oversample);

// Process audio
void circuit_process(CircuitContext* ctx, 
                     const float* input, 
                     float* output, 
                     int num_samples, 
                     int num_channels);

// Set parameters
void circuit_set_parameter(CircuitContext* ctx, const char* name, double value);

// Cleanup
void circuit_cleanup(CircuitContext* ctx);
```

## Testing the Export

After exporting, test the circuit:

```bash
# Create test audio
sox -n -r 48000 -b 16 test.wav synth 3 sine 440

# Process through exported circuit
./circuit_test -i test.wav -c exported_circuit.dylib -o output.wav -m
```

## Current Limitations

- **Simplified Code Generation**: Currently generates template code that approximates the circuit behavior
- **Full Expression Export**: To be implemented - exporting actual symbolic expressions from LiveSPICE
- **Parameter Mapping**: Basic parameter support (Gain, Distortion, Volume)

## Future Enhancements

1. **Full Symbolic Export** - Export actual Newton-Raphson solver code from LiveSPICE
2. **Multi-channel Support** - Stereo/mono handling
3. **Custom Parameters** - Map actual potentiometer names from schematic
4. **Optimization** - SIMD vectorization, fixed-point math option
5. **Validation** - Compare output with LiveSPICE reference

## Integration with Build Pipeline

```bash
#!/bin/bash
# build_circuit.sh

CIRCUIT_NAME="Marshall Blues Breaker"
SCHX_FILE="Tests/Examples/Marshall Blues Breaker.schx"
OUTPUT_DIR="circuits"

# Export to C
dotnet run --project ExportToC -- \
  --input "$SCHX_FILE" \
  --output "$OUTPUT_DIR/blues_breaker.c"

# Compile for multiple platforms
clang -dynamiclib -arch x86_64 -arch arm64 \
  -o "$OUTPUT_DIR/blues_breaker.dylib" \
  "$OUTPUT_DIR/blues_breaker.c" -O2 -lm

echo "Built: $OUTPUT_DIR/blues_breaker.dylib"
```

## License

MIT (same as LiveSPICE)