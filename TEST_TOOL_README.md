# Circuit Test Tool

A command-line tool for testing LiveSPICE-compiled circuit dylibs with audio files.

## Overview

This tool loads a circuit dynamic library (`.dylib` on macOS, `.so` on Linux) and processes audio files through it. It measures:
- Processing latency
- DSP load (% of real-time)
- Real-time performance ratio

## Files

- `circuit_api.h` - C API interface for circuit dylibs
- `circuit_test.c` - CLI test tool implementation
- `sample_circuit.c` - Example circuit implementation (tube-style distortion)
- `Makefile` - Build automation

## Building

### Prerequisites

**macOS:**
```bash
brew install libsndfile
```

**Linux (Ubuntu/Debian):**
```bash
sudo apt-get install libsndfile1-dev
```

### Compile

```bash
make
```

This creates:
- `circuit_test` - The CLI tool
- `sample_circuit.dylib` (or `.so`) - Sample circuit

## Usage

### Basic Test

```bash
./circuit_test \
  --input input.wav \
  --circuit sample_circuit.dylib \
  --output output.wav
```

### With Parameters

```bash
./circuit_test \
  --input input.wav \
  --circuit sample_circuit.dylib \
  --output output.wav \
  --param Gain=0.8 \
  --param Distortion=0.6 \
  --param Volume=0.9
```

### Performance Measurement

```bash
./circuit_test \
  --input input.wav \
  --circuit sample_circuit.dylib \
  --output output.wav \
  --buffer-size 256 \
  --oversample 8 \
  --measure-latency \
  --verbose
```

## Options

| Option | Description | Default |
|--------|-------------|---------|
| `-i, --input FILE` | Input WAV file | Required |
| `-c, --circuit FILE` | Circuit dylib file | Required |
| `-o, --output FILE` | Output WAV file | Required |
| `-r, --sample-rate RATE` | Sample rate | 48000 |
| `-b, --buffer-size SIZE` | Buffer size (samples) | 256 |
| `-v, --oversample N` | Oversampling factor | 8 |
| `-p, --param NAME=VALUE` | Set parameter (can use multiple) | None |
| `-m, --measure-latency` | Measure latency | Off |
| `-V, --verbose` | Verbose output | Off |
| `-h, --help` | Show help | - |

## Example Output

```
Circuit Test Tool v1.0
======================

Configuration:
  Input: guitar.wav
  Circuit: sample_circuit.dylib
  Output: distorted_guitar.wav
  Sample rate: 48000 Hz
  Buffer size: 256 samples
  Oversample: 8x

Input file: guitar.wav
  Sample rate: 48000 Hz
  Channels: 1
  Frames: 144000

Circuit initialized: Simple Tube Distortion
  Sample rate: 48000 Hz
  Buffer size: 256 samples
  Oversample: 8x

Circuit: Simple Tube Distortion
  Description: Basic tube emulation for testing
  Inputs: 1, Outputs: 1

Processing audio...

Processing complete!
  Output file: distorted_guitar.wav
  Total frames: 144000
  Audio duration: 3.000 seconds
  Processing time: 0.150 seconds
  Real-time ratio: 20.00x
  DSP Load: 5.0%
  Latency: 5.33 ms (256 samples @ 48000 Hz)
  Buffer count: 563

Latency Analysis:
  Buffer latency: 5.33 ms
  Recommended for real-time: < 10 ms
```

## Creating Your Own Circuits

To create a circuit dylib that works with this tool:

1. Implement the C API defined in `circuit_api.h`
2. Export these functions:
   - `circuit_init()` - Initialize circuit state
   - `circuit_process()` - Process audio buffer
   - `circuit_cleanup()` - Free resources
   - `circuit_get_info()` - Return circuit metadata
   - `circuit_set_parameter()` - Set control values

See `sample_circuit.c` for a complete example.

## Testing with the Export to C Tool

Once you have the "export to C" tool working with LiveSPICE:

```bash
# 1. Generate C code from .schx
./export_to_c --input circuit.schx --output circuit.c

# 2. Compile to dylib
make circuit.dylib

# 3. Test with audio
./circuit_test -i test.wav -c circuit.dylib -o output.wav -m
```

## Performance Targets

For real-time guitar processing:
- **Latency**: < 10ms (256 samples @ 48kHz = 5.33ms)
- **DSP Load**: < 50% (leaves headroom for other effects)
- **Real-time ratio**: > 2x (LiveSPICE achieves 15-20x)

## Troubleshooting

**"Error loading circuit"**
- Verify the dylib file exists and has correct architecture
- Check with `file circuit.dylib` (macOS/Linux)

**"Error opening input file"**
- Ensure input is a valid WAV file
- Check file permissions

**High DSP load**
- Try smaller buffer size (e.g., 128)
- Reduce oversampling (e.g., 4x instead of 8x)
- Circuit may be too complex for real-time

## License

MIT (same as LiveSPICE)