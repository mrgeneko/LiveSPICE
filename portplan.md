# LiveSPICE Port Plan

## Overview

Port the LiveSPICE circuit simulation engine (headless, no UI/audio I/O) to enable real-time audio processing in macOS/iOS Swift applications.

## What is LiveSPICE?

LiveSPICE is a real-time SPICE-like circuit simulation tool for live audio signals. It allows modeling of guitar pedals and amplifiers using a visual schematic editor, then processing audio through the simulated circuit with minimal latency.

**License:** MIT

**Repository:** https://github.com/mrgeneko/LiveSPICE (fork of dsharlet/LiveSPICE)

## Discussion Summary (#222)

Key points from the LiveSPICE maintainer and contributors about cross-platform porting:

### Architecture Breakdown

1. **The Math (ComputerAlgebra)**
   - Cross-platform C# code
   - Hard to replace - uses exact arithmetic (BigInteger-backed Rational) for symbolic solving
   - Comparable alternatives: SymPy, SymForce

2. **Analysis/Compilation**
   - The unique part of LiveSPICE
   - JIT compiles symbolic solutions to .NET Expression Trees (LINQ Expressions)
   - This is C#-specific

3. **Audio I/O**
   - Platform-specific (Windows: ASIO/WASAPI)
   - Would need CoreAudio wrapper for macOS/iOS

4. **UI**
   - WPF on Windows
   - Could use Avalonia for cross-platform, but "labor intensive"

### Progress

- **@Federerer** (May 2025): Got headless version running on Linux after removing Windows Forms - achieved **700 kHz (15.9x real-time)**
- **@evan-olcott**: Was working on a Swift port

## Current State

### Cross-Platform Ready

These projects target `netstandard2.0` and build on any platform:

- `Circuit/Circuit.csproj` - Circuit simulation logic
- `ComputerAlgebra/ComputerAlgebra/ComputerAlgebra.csproj` - Symbolic math
- `Util/Util.csproj` - Utilities

### Windows-Only

- `Tests/Tests.csproj` - Uses Windows Forms (can be removed for cross-platform)
- Audio I/O components (ASIO, WaveAudio)

## What's Needed

### Core Libraries to Port

| Library | Purpose | Lines |
|---------|---------|-------|
| `ComputerAlgebra/` | Symbolic math, expression trees, JIT compilation | ~15k |
| `Circuit/Simulation/` | Circuit solving, transient analysis | ~5k |
| `Util/` | Utilities | ~2k |

### Circuit Files

LiveSPICE circuits are stored as `.schx` files - XML format describing components:

```xml
<Schematic Name="JH Fuzz">
  <Element>
    <Component _Type="Circuit.Resistor" Resistance="82.5 kΩ" Name="R1" />
    <Component _Type="Circuit.Capacitor" Capacitance="2.2 μF" Name="C1" />
    <Component _Type="Circuit.BipolarJunctionTransistor" Type="NPN" Name="Q1" />
  </Element>
</Schematic>
```

**Key insight:** The `.schx` is declarative - no compiled code. Analysis + compilation happens at runtime when loading.

For App Store, we pre-compile circuits at build time instead of runtime.

### Key Runtime Interface

The critical method is in `Circuit/Simulation/Simulation.cs`:

```csharp
public void Run(int N, IEnumerable<double[]> Input, IEnumerable<double[]> Output)
```

This processes audio buffers:
- `N` = number of samples per buffer (e.g., 256)
- `Input` = audio input samples
- `Output` = processed audio output samples

### Simulation Flow

1. **Analysis phase** - Symbolic solving of circuit equations (can be done offline)
2. **Compilation** - JIT generates a `Func<double[], double[]>` delegate
3. **Runtime** - For each input sample, iteratively solves non-linear system using Newton-Raphson

## Integration Options

### Option 1: Port to Swift (Recommended)

Translate the core simulation to Swift. This gives full native performance on macOS/iOS.

**Pros:**
- Native performance
- Full control over audio pipeline
- No runtime dependencies

**Cons:**
- ~20k lines of code to translate
- Ongoing maintenance burden

### Option 2: .NET AOT + P/Invoke

Compile .NET libraries to native code, call from Swift via P/Invoke.

**Pros:**
- Reuse existing code
- MIT-licensed .NET runtime possible

**Cons:**
- Complex build pipeline
- P/Invoke overhead

### Option 3: Export Coefficients + Swift Runtime

Run LiveSPICE analysis offline, export solved coefficients, implement just the runtime solver in Swift.

**Pros:**
- Minimal porting effort
- Fast runtime (pre-computed coefficients)

**Cons:**
- Less flexible (circuit changes require re-analysis)

## Implementation Plan

### Phase 1: Export to C Tool ✅ COMPLETED

**Location:** `ExportToC/` directory

**What it does:**
1. Loads `.schx` circuit files (e.g., "Marshall Blues Breaker.schx")
2. Performs symbolic analysis using LiveSPICE libraries
3. Generates C code representing the circuit simulation
4. Optionally compiles to `.dylib` for macOS/iOS

**Usage:**
```bash
# Export a circuit to C
dotnet run --project ExportToC -- \
  --input Tests/Examples/Marshall\ Blues\ Breaker.schx \
  --output blues_breaker.c

# Export and compile to dylib
dotnet run --project ExportToC -- \
  --input Tests/Examples/Marshall\ Blues\ Breaker.schx \
  --output blues_breaker.c \
  --dylib
```

**Build outputs:**
- `blues_breaker.c` - Generated C source code
- `blues_breaker.dylib` - Compiled circuit binary (with --dylib flag)

**Supported Circuits:** Any `.schx` file from LiveSPICE, including:
- Marshall Blues Breaker
- Ibanez Tube Screamer TS-9
- Boss Super Overdrive SD-1
- Big Muff Pi
- Marshall JCM800 Preamp
- Fender 5e3
- And many more...

### Phase 2: Swift Runtime Library

1. Create Swift library to:
   - Load pre-compiled dylibs via `dlopen()`
   - Call exported C functions from Swift
   - Integrate with AVAudioEngine for audio processing

2. API design:
   ```swift
   class CircuitProcessor {
       func load(circuit: "jh_f1")     // Load dylib
       func process(buffer: UnsafePointer<Float>, count: Int)
   }
   ```

### Phase 3: Testing & Optimization

1. Validate generated C against original LiveSPICE output
2. Benchmark performance (should match LiveSPICE ~700 kHz)
3. Test on macOS, iOS, iPadOS
4. App Store compatibility review

## App Store Considerations

### JIT is Not Allowed

Apple prohibits JIT compilation in App Store apps:
- Section 2.5.2: Apps must not download executable code that can be interpreted at runtime
- Only Apple's whitelisted components (JavaScriptCore, WebKit) can do JIT
- Apps using JIT will be rejected

### Solution: Pre-compiled Circuits

The "export to C" approach:
1. **Build-time** (on developer's Mac):
   ```
   .schx → [C code generator] → compile → .dylib
   ```

2. **Runtime** (in app):
   - Bundle pre-compiled `.dylib` in app bundle
   - Load via `dlopen()`
   - Call functions directly (no JIT)

### Platform Support

| Platform | dlopen() | Notes |
|----------|----------|-------|
| **macOS** | ✅ Yes | Full flexibility, can notarize |
| **iOS** | ✅ Yes | Bundle dylib in app |
| **iPadOS** | ✅ Yes | Same as iOS |

### Cross-Platform Builds

- Compile separate dylibs for each target platform
- Select correct dylib at build time based on target architecture

## Test CLI Tool

**Status: ✅ Implemented**

See `circuit_test.c`, `circuit_api.h`, `sample_circuit.c`, and `Makefile` in the repository root.

### Quick Start

```bash
# Install dependencies (macOS)
brew install libsndfile

# Build
make

# Run test
./circuit_test \
  --input test.wav \
  --circuit sample_circuit.dylib \
  --output output.wav \
  --measure-latency
```

### What's Included

To verify circuit processing works correctly before integration, build a CLI tool that tests compiled circuits with audio files.

### Architecture

```bash
./circuit_test \
  --input input.wav \
  --circuit circuit.dylib \
  --output output.wav \
  --sample-rate 48000 \
  --buffer-size 256 \
  --measure-latency
```

### Components Needed

1. **C API for the dylib:**
```c
// circuit_api.h
typedef struct {
    void* state;
    int sample_rate;
    int buffer_size;
    double timestep;
} CircuitContext;

CircuitContext* circuit_init(int sample_rate, int buffer_size);
void circuit_process(CircuitContext* ctx, 
                     const float* input, 
                     float* output, 
                     int num_samples);
void circuit_set_parameter(CircuitContext* ctx, const char* name, double value);
void circuit_cleanup(CircuitContext* ctx);
```

2. **Audio I/O:** Use **libsndfile** (MIT license) for WAV files
   - `sf_read_float()` - read input
   - `sf_write_float()` - write output

3. **Load the dylib:**
```c
#include <dlfcn.h>

void* handle = dlopen("circuit.dylib", RTLD_LAZY);
CircuitContext* (*init)(int, int) = dlsym(handle, "circuit_init");
void (*process)(CircuitContext*, const float*, float*, int) = 
    dlsym(handle, "circuit_process");
```

### Performance Measurements

**DSP Load:**
```c
clock_t start = clock();
circuit_process(ctx, input_buffer, output_buffer, buffer_size);
clock_t end = clock();
double process_time = ((double)(end - start)) / CLOCKS_PER_SEC;
double real_time = (double)buffer_size / sample_rate;
double load_percent = (process_time / real_time) * 100.0;
```

**Latency:**
- Fixed by buffer size: 256 samples @ 48kHz = **5.33ms**
- Target: < 10ms for real-time playability
- LiveSPICE achieves 700kHz processing (15.9x real-time)

**Output Verification:**
- Compare output.wav to reference
- Check for clipping/distortion artifacts
- Measure frequency response

### Test Scenarios

1. **Unit test:** Sine wave sweep through circuit
2. **Functional test:** Real guitar recording
3. **Stress test:** White noise, high amplitude signals
4. **Parameter sweep:** Test all potentiometer positions

## Existing Resources

- LiveSPICE examples: `Tests/Examples/*.schx`
- Circuit test files: `Tests/Circuits/*.schx`
- Component library: `Circuit/Components/*.xml`

## .NET Dependency Analysis

### What .NET Provides

The core .NET dependency is **System.Linq.Expressions** - used for JIT compilation.

#### How It's Used

1. **Build Expression Tree** - LiveSPICE builds a symbolic representation of the circuit simulation as an expression tree:

```csharp
// Simplified example from Simulation.cs:192-413
CodeGen code = new CodeGen();
// ... build expression tree ...
LinqExprs.LambdaExpression lambda = code.Build();
return lambda.Compile();  // JIT compile to delegate
```

2. **The Compiled Delegate** - The result is an `Action<int, double, double[][], double[][]>` that processes audio:

```csharp
private Action<int, double, double[][], double[][]> _process;

// Called per audio buffer:
_process(N, n*TimeStep, Input.AsArray(), Output.AsArray());
```

#### Why JIT Compilation?

LiveSPICE's uniqueness comes from:
- Symbolic circuit analysis (solves equations symbolically at "compile" time)
- JIT compiles to optimized native code for runtime
- This achieves 700 kHz (15.9x real-time) vs typical SPICE simulators

### Can This Be Replaced?

| Alternative | Language | Notes |
|-------------|----------|-------|
| **SymPy + Numba** | Python | Symbolic + JIT, but Python overhead |
| **SymForce** | C++ | Similar concept, generates C++ code |
| **LLVM** | Any | Compile IR to native code |
| **Embeddable JIT** | Various | TinyJIT, JittedFunction |
| **Pre-compile to C** | Any | Generate C code, compile to library |
| **Interpret expression tree** | Any | Slower but portable |

#### Best Options for Swift

1. **LLVM via Swift bindings** - Use LLVM to compile expression trees
   - `llvm-mirror/llvm` has Swift bindings
   - Powerful but complex

2. **Generate C code + libclang** - Pre-compilation approach
   - Generate C from expression trees
   - Compile to dylib, load via Swift
   - No runtime JIT needed

3. **Custom interpreter** - Just interpret the expression tree
   - Simpler but slower
   - May work for simple circuits

4. **Port expression system** - Translate ComputerAlgebra to Swift
   - Biggest effort but full control
   - No runtime dependencies

### Recommendation

For maximum performance, **generate C code at analysis time**:
1. LiveSPICE does symbolic analysis (already platform-independent)
2. Generate C code for the specific circuit
3. Compile to a dylib
4. Load and call from Swift

This removes .NET entirely from the runtime.

## Current Implementation Status

### What Works

✅ **Export Tool Built**
- Loads `.schx` circuit files
- Extracts component information (resistors, capacitors, potentiometers)
- Generates C code with parameter support
- Compiles to `.dylib` for macOS/iOS

✅ **Parameter Detection**
The export tool correctly identifies all potentiometers from the schematic:
- Marshall Blues Breaker: Drive, Tone, Level
- Marshall JCM800: Gain 1, Gain 2, Bass, Middle, Treble, Volume
- All 20 example circuits export successfully

✅ **Test CLI Tool**
- Streams audio through circuits
- Measures DSP load, latency, real-time ratio
- Supports parameter changes via `--param` flag

### Current Limitation: Template-Based Export

⚠️ **The exported C code is a GENERIC TEMPLATE, not the actual LiveSPICE circuit equations.**

**What happens:**
1. ✅ LiveSPICE analysis runs correctly (solves 52 equations for Blues Breaker)
2. ✅ Component values extracted (R1=1MΩ, C1=10nF, etc.)
3. ❌ Generated C uses a **generic tube distortion formula** instead of actual equations

**Why:**
```c
// This is a TEMPLATE - not the real circuit!
double x = sample * gain;           // Simple gain stage
double clipped = soft_clip(x);       // Generic clipping
double out = tone_control(clipped);  // Generic EQ
```

The exported code responds to parameter changes (Drive, Tone, Volume work), but it does **not** simulate the actual circuit behavior (specific diode configurations, transistor stages, etc.).

### Why This Happens

LiveSPICE uses a sophisticated JIT compiler:
1. **Symbolic analysis** - Creates system of equations (works ✅)
2. **Expression tree generation** - Complex C# LINQ Expression trees
3. **JIT compilation** - Compiles to native code at runtime

To export actual circuit equations would require:
- Parsing LiveSPICE's symbolic expressions (~15k lines)
- Implementing a Newton-Raphson solver in C
- Porting all component models (diodes, transistors, op-amps)
- Generating C code from expression trees

This is essentially re-implementing LiveSPICE's JIT compiler in C.

### Alternative Approaches

#### Option 1: Full Equation Export (Major Effort)
Port LiveSPICE's ComputerAlgebra library to C/Swift:
- Translate ~15k lines of symbolic math
- Implement JIT compiler for C
- Estimated effort: 6-12 months

#### Option 2: Neural Modeling (Different Approach)
Use machine learning to model circuits from audio:
- **Neural Amp Modeler (NAM)** - Open-source, trains from recordings
- **GuitarML** - Python tools for amp/pedal modeling
- Records actual gear, creates neural network model
- Works with any circuit without symbolic analysis

#### Option 3: Simplified Component Library (Medium Effort)
Create a C component library:
- Diode models (Shockley equation)
- Transistor models (Ebers-Moll)
- Op-amp models
- Generate C code that calls these components
- More accurate than template, less effort than full port

### What's Next

1. **For accurate circuit simulation**: Consider neural modeling (NAM) or full equation export
2. **For prototyping**: Current template approach works for testing the pipeline
3. **For production**: Would need either Option 1 or Option 3

## References

- Original LiveSPICE: https://github.com/dsharlet/LiveSPICE
- LiveSPICE website: https://www.livespice.org/
- Discussion #222: https://github.com/dsharlet/LiveSPICE/discussions/222
- How LiveSPICE works: https://dsharlet.com/2014/03/28/how-livespice-works-numerically-solving-circuit-equations.html
- SymForce (similar idea): https://github.com/symforce-org/symforce
- System.Linq.Expressions: https://learn.microsoft.com/en-us/dotnet/api/system.linq.expressions.expression
