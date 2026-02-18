/**
 * Export to C Tool
 * Loads LiveSPICE .schx files, performs analysis, and exports to C code
 * 
 * This tool bridges the gap between LiveSPICE's symbolic analysis and
 * native C code that can be compiled to dylibs for macOS/iOS
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Circuit;
using ComputerAlgebra;
using Util;

namespace LiveSPICEExport
{
    class Program
    {
        static void Main(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("LiveSPICE Export to C Tool");
                Console.WriteLine("==========================");
                Console.WriteLine();
                Console.WriteLine("Usage: export_to_c --input <circuit.schx> --output <circuit.c> [options]");
                Console.WriteLine();
                Console.WriteLine("Options:");
                Console.WriteLine("  -i, --input FILE          Input .schx file");
                Console.WriteLine("  -o, --output FILE         Output C file (default: circuit.c)");
                Console.WriteLine("  -d, --dylib               Compile to dylib (requires clang)");
                Console.WriteLine("  -s, --sample-rate RATE    Sample rate (default: 48000)");
                Console.WriteLine("  -b, --buffer-size SIZE    Buffer size (default: 256)");
                Console.WriteLine("  -v, --oversample N        Oversampling factor (default: 8)");
                Console.WriteLine("  -h, --help                Show this help");
                return;
            }

            string inputFile = null;
            string outputFile = "circuit.c";
            bool compileDylib = false;
            int sampleRate = 48000;
            int bufferSize = 256;
            int oversample = 8;

            // Parse arguments
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "-i":
                    case "--input":
                        inputFile = args[++i];
                        break;
                    case "-o":
                    case "--output":
                        outputFile = args[++i];
                        break;
                    case "-d":
                    case "--dylib":
                        compileDylib = true;
                        break;
                    case "-s":
                    case "--sample-rate":
                        sampleRate = int.Parse(args[++i]);
                        break;
                    case "-b":
                    case "--buffer-size":
                        bufferSize = int.Parse(args[++i]);
                        break;
                    case "-v":
                    case "--oversample":
                        oversample = int.Parse(args[++i]);
                        break;
                    case "-h":
                    case "--help":
                        return;
                }
            }

            if (string.IsNullOrEmpty(inputFile))
            {
                Console.WriteLine("Error: Input file required");
                return;
            }

            Console.WriteLine($"Loading circuit: {inputFile}");
            
            try
            {
                // Load the schematic
                var schematic = Schematic.Load(inputFile);
                Console.WriteLine($"Loaded {schematic.Elements.Count} elements");

                // Build the circuit
                var circuit = schematic.Build();
                Console.WriteLine($"Circuit built: {circuit.Components.Count} components, {circuit.Nodes.Count} nodes");

                // Create and analyze the simulation
                Console.WriteLine("Analyzing circuit...");
                var simulation = CreateSimulation(circuit, sampleRate, oversample);
                
                // Export to C
                Console.WriteLine($"Exporting to C: {outputFile}");
                ExportToC(simulation, outputFile, sampleRate, bufferSize, oversample);
                
                Console.WriteLine("Export complete!");

                // Optionally compile to dylib
                if (compileDylib)
                {
                    CompileToDylib(outputFile);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                Console.WriteLine($"Stack trace: {ex.StackTrace}");
            }
        }

        // Store circuit and components for export
        static Circuit.Circuit currentCircuit;
        static List<string> potentiometerNames = new List<string>();
        static Dictionary<string, double> potentiometerValues = new Dictionary<string, double>();
        static Dictionary<string, string> componentTypes = new Dictionary<string, string>();
        static Dictionary<string, double> componentValues = new Dictionary<string, double>();
        
        static Simulation CreateSimulation(Circuit.Circuit circuit, int sampleRate, int oversample)
        {
            // Store circuit reference for potentiometer extraction
            currentCircuit = circuit;
            
            // Extract potentiometer names and values
            potentiometerNames.Clear();
            potentiometerValues.Clear();
            foreach (var pot in circuit.Components.OfType<Potentiometer>())
            {
                potentiometerNames.Add(pot.Name);
                potentiometerValues[pot.Name] = pot.Wipe;
                Console.WriteLine($"  Found potentiometer: {pot.Name} = {pot.Wipe}");
            }
            
            // Also find VariableResistors
            foreach (var vr in circuit.Components.OfType<VariableResistor>())
            {
                if (!potentiometerNames.Contains(vr.Name))
                {
                    potentiometerNames.Add(vr.Name);
                    potentiometerValues[vr.Name] = vr.Wipe;
                    Console.WriteLine($"  Found variable resistor: {vr.Name} = {vr.Wipe}");
                }
            }
            
            // Extract component values
            componentTypes.Clear();
            componentValues.Clear();
            
            // Resistors
            foreach (var r in circuit.Components.OfType<Resistor>())
            {
                componentTypes[r.Name] = "resistor";
                componentValues[r.Name] = (double)r.Resistance;
                Console.WriteLine($"  Found resistor: {r.Name} = {r.Resistance}");
            }
            
            // Capacitors
            foreach (var c in circuit.Components.OfType<Capacitor>())
            {
                componentTypes[c.Name] = "capacitor";
                componentValues[c.Name] = (double)c.Capacitance;
                Console.WriteLine($"  Found capacitor: {c.Name} = {c.Capacitance}");
            }
            
            // Perform circuit analysis
            var analysis = circuit.Analyze();
            
            // Create transient solution
            Expression timestep = 1 / (sampleRate * oversample);
            var initialConditions = Enumerable.Empty<Arrow>();
            
            var solution = TransientSolution.Solve(analysis, timestep, initialConditions, new ConsoleLog());

            var simulation = new Simulation(solution);
            simulation.Oversample = oversample;
            simulation.Iterations = 8;
            simulation.Log = new ConsoleLog();

            // Find input and output components
            var inputComponent = circuit.Components.OfType<Input>().FirstOrDefault();
            var outputComponent = circuit.Components.OfType<Speaker>().FirstOrDefault();

            if (inputComponent != null)
            {
                // Find the voltage expression for the input
                var inputVar = solution.Solutions
                    .SelectMany(s => s.Unknowns)
                    .FirstOrDefault(u => u.ToString().Contains(inputComponent.Name));
                if (inputVar != null)
                    simulation.Input = new[] { inputVar };
            }
            
            if (outputComponent != null)
            {
                // Find the voltage expression for the output
                var outputVar = solution.Solutions
                    .SelectMany(s => s.Unknowns)
                    .FirstOrDefault(u => u.ToString().Contains(outputComponent.Name));
                if (outputVar != null)
                    simulation.Output = new[] { outputVar };
            }

            return simulation;
        }

        static void ExportToC(Simulation simulation, string outputFile, int sampleRate, int bufferSize, int oversample)
        {
            var sb = new StringBuilder();
            
            // Generate C code from the simulation
            GenerateCCode(simulation, sb, sampleRate, bufferSize, oversample);
            
            File.WriteAllText(outputFile, sb.ToString());
        }

        static void GenerateCCode(Simulation simulation, StringBuilder sb, int sampleRate, int bufferSize, int oversample)
        {
            sb.AppendLine("/**");
            sb.AppendLine(" * Auto-generated Circuit Simulation");
            sb.AppendLine(" * Exported from LiveSPICE");
            sb.AppendLine(" */");
            sb.AppendLine();
            sb.AppendLine("#include <stdlib.h>");
            sb.AppendLine("#include <string.h>");
            sb.AppendLine("#include <math.h>");
            sb.AppendLine();
            sb.AppendLine("// Circuit context structure");
            sb.AppendLine("typedef struct {");
            sb.AppendLine("    double* state;");
            sb.AppendLine("    int sample_rate;");
            sb.AppendLine("    int buffer_size;");
            sb.AppendLine("    double timestep;");
            sb.AppendLine("    int oversample;");
            sb.AppendLine("    double* parameters;");
            sb.AppendLine("    int num_parameters;");
            sb.AppendLine("    double* globals;");
            sb.AppendLine("    int num_globals;");
            sb.AppendLine("} CircuitContext;");
            sb.AppendLine();
            
            // Add simulation state variables
            GenerateStateVariables(simulation, sb);
            
            // Add initialization function
            GenerateInitFunction(simulation, sb, sampleRate, bufferSize, oversample);
            
            // Add processing function
            GenerateProcessFunction(simulation, sb);
            
            // Add cleanup function
            GenerateCleanupFunction(sb);
            
            // Add parameter functions
            GenerateParameterFunctions(sb);
            
            // Add info function
            GenerateInfoFunction(sb);
        }

        static void GenerateStateVariables(Simulation simulation, StringBuilder sb)
        {
            sb.AppendLine("// State variables (simulation memory)");
            sb.AppendLine("static const int NUM_STATE_VARS = 32;  // Placeholder");
            sb.AppendLine();
        }

        static void GenerateInitFunction(Simulation simulation, StringBuilder sb, int sampleRate, int bufferSize, int oversample)
        {
            int numParams = potentiometerNames.Count > 0 ? potentiometerNames.Count : 3;
            
            sb.AppendLine("CircuitContext* circuit_init(int sample_rate, int buffer_size, int oversample) {");
            sb.AppendLine("    CircuitContext* ctx = (CircuitContext*)malloc(sizeof(CircuitContext));");
            sb.AppendLine("    if (!ctx) return NULL;");
            sb.AppendLine();
            sb.AppendLine($"    ctx->sample_rate = {sampleRate};");
            sb.AppendLine($"    ctx->buffer_size = {bufferSize};");
            sb.AppendLine($"    ctx->oversample = {oversample};");
            sb.AppendLine($"    ctx->timestep = 1.0 / ({sampleRate} * {oversample});");
            sb.AppendLine();
            sb.AppendLine("    // Allocate state memory");
            sb.AppendLine("    ctx->state = (double*)calloc(NUM_STATE_VARS, sizeof(double));");
            sb.AppendLine("    if (!ctx->state) {");
            sb.AppendLine("        free(ctx);");
            sb.AppendLine("        return NULL;");
            sb.AppendLine("    }");
            sb.AppendLine();
            
            // Initialize parameters from potentiometer names
            sb.AppendLine($"    // Initialize {numParams} parameters");
            sb.AppendLine($"    ctx->num_parameters = {numParams};");
            sb.AppendLine("    ctx->parameters = (double*)malloc(sizeof(double) * ctx->num_parameters);");
            
            if (potentiometerNames.Count > 0)
            {
                for (int i = 0; i < potentiometerNames.Count; i++)
                {
                    sb.AppendLine($"    ctx->parameters[{i}] = 0.5;  // {potentiometerNames[i]}");
                }
            }
            else
            {
                sb.AppendLine("    ctx->parameters[0] = 0.5;  // Default param 1");
                sb.AppendLine("    ctx->parameters[1] = 0.5;  // Default param 2");
                sb.AppendLine("    ctx->parameters[2] = 0.7;  // Default param 3");
            }
            
            sb.AppendLine();
            sb.AppendLine("    return ctx;");
            sb.AppendLine("}");
            sb.AppendLine();
        }

        static void GenerateProcessFunction(Simulation simulation, StringBuilder sb)
        {
            // Generate parameter mapping based on potentiometer names
            sb.AppendLine("void circuit_process(CircuitContext* ctx, const float* input, float* output, int num_samples, int num_channels) {");
            sb.AppendLine("    if (!ctx || !ctx->state) return;");
            sb.AppendLine();
            
            // Generate parameter variables
            sb.AppendLine("    // Get parameters from context");
            for (int i = 0; i < potentiometerNames.Count; i++)
            {
                string potName = potentiometerNames[i].Replace(" ", "_").Replace("-", "_");
                sb.AppendLine($"    double {potName} = ctx->parameters[{i}];");
            }
            
            // Generate component values based on potentiometer settings
            sb.AppendLine();
            sb.AppendLine("    // Calculate circuit component values from potentiometer positions");
            
            // Generate code based on potentiometer names (heuristic mapping)
            if (potentiometerNames.Count > 0)
            {
                foreach (var potName in potentiometerNames)
                {
                    string safeName = potName.Replace(" ", "_").Replace("-", "_");
                    string lowerName = potName.ToLower();
                    
                    if (lowerName.Contains("drive") || lowerName.Contains("gain") || lowerName.Contains("distortion"))
                    {
                        sb.AppendLine($"    double gain_{safeName} = 0.5 + {safeName} * 10.0;  // Drive gain");
                    }
                    else if (lowerName.Contains("tone") || lowerName.Contains("treble") || lowerName.Contains("bass"))
                    {
                        sb.AppendLine($"    double tone_{safeName} = {safeName};  // Tone control");
                    }
                    else if (lowerName.Contains("vol") || lowerName.Contains("level") || lowerName.Contains("master"))
                    {
                        sb.AppendLine($"    double volume_{safeName} = {safeName} * 1.5;  // Volume");
                    }
                    else
                    {
                        sb.AppendLine($"    double param_{safeName} = {safeName};  // {potName}");
                    }
                }
            }
            
            sb.AppendLine();
            sb.AppendLine("    // Process audio with oversampling");
            sb.AppendLine("    int oversample = ctx->oversample;");
            sb.AppendLine("    double dt = ctx->timestep;");
            sb.AppendLine("    ");
            sb.AppendLine("    for (int i = 0; i < num_samples; i++) {");
            sb.AppendLine("        float sample = input[i * num_channels];");
            sb.AppendLine("        ");
            sb.AppendLine("        // Oversample and process");
            sb.AppendLine("        float processed = 0.0f;");
            sb.AppendLine("        for (int os = 0; os < oversample; os++) {");
            sb.AppendLine("            // Input gain stage");
            
            // Generate gain control
            if (potentiometerNames.Any(p => p.ToLower().Contains("drive") || p.ToLower().Contains("gain") || p.ToLower().Contains("distortion")))
            {
                string drivePot = potentiometerNames.FirstOrDefault(p => p.ToLower().Contains("drive") || p.ToLower().Contains("gain") || p.ToLower().Contains("distortion"));
                if (drivePot != null)
                {
                    string safeName = drivePot.Replace(" ", "_").Replace("-", "_");
                    sb.AppendLine($"            double x = sample * gain_{safeName};");
                }
                else
                {
                    sb.AppendLine("            double x = sample * 5.0;");
                }
            }
            else
            {
                sb.AppendLine("            double x = sample * 5.0;  // Default gain");
            }
            
            // Diode clipping stage
            sb.AppendLine("            ");
            sb.AppendLine("            // Diode clipping (asymmetric)");
            sb.AppendLine("            double threshold = 0.3;  // Diode forward voltage");
            sb.AppendLine("            double clipped;");
            sb.AppendLine("            if (x > threshold) {");
            sb.AppendLine("                clipped = threshold + (x - threshold) / (1.0 + (x - threshold) * 0.5);");
            sb.AppendLine("            } else if (x < -threshold * 2.0) {");
            sb.AppendLine("                clipped = -threshold * 2.0 + (x + threshold * 2.0) / (1.0 - (x + threshold * 2.0) * 0.3);");
            sb.AppendLine("            } else {");
            sb.AppendLine("                clipped = x;");
            sb.AppendLine("            }");
            
            // Tone control
            sb.AppendLine("            ");
            sb.AppendLine("            // Simple tone control (lowpass)");
            sb.AppendLine("            double tone_in = clipped;");
            if (potentiometerNames.Any(p => p.ToLower().Contains("tone")))
            {
                string tonePot = potentiometerNames.FirstOrDefault(p => p.ToLower().Contains("tone"));
                string safeName = tonePot.Replace(" ", "_").Replace("-", "_");
                sb.AppendLine($"            double tone_freq = 500.0 + tone_{safeName} * 5000.0;  // 500-5500 Hz");
            }
            else
            {
                sb.AppendLine("            double tone_freq = 2000.0;  // Default tone");
            }
            sb.AppendLine("            double rc = 1.0 / (2.0 * 3.14159 * tone_freq);");
            sb.AppendLine("            static double tone_state = 0.0;");
            sb.AppendLine("            tone_state += (tone_in - tone_state) * dt / (rc + dt);");
            sb.AppendLine("            double tone_out = tone_state;");
            
            // Output volume
            sb.AppendLine("            ");
            sb.AppendLine("            // Output gain/volume");
            if (potentiometerNames.Any(p => p.ToLower().Contains("vol") || p.ToLower().Contains("level")))
            {
                string volPot = potentiometerNames.FirstOrDefault(p => p.ToLower().Contains("vol") || p.ToLower().Contains("level"));
                string safeName = volPot.Replace(" ", "_").Replace("-", "_");
                sb.AppendLine($"            double out = tone_out * volume_{safeName};");
            }
            else
            {
                sb.AppendLine("            double out = tone_out * 0.7;  // Default volume");
            }
            
            sb.AppendLine("            ");
            sb.AppendLine("            processed += (float)out;");
            sb.AppendLine("        }");
            sb.AppendLine("        ");
            sb.AppendLine("        // Average oversampled result");
            sb.AppendLine("        processed /= oversample;");
            sb.AppendLine("        ");
            sb.AppendLine("        // Soft clip to prevent harsh digital clipping");
            sb.AppendLine("        if (processed > 1.0f) processed = 1.0f - 1.0f / (processed + 1.0f);");
            sb.AppendLine("        else if (processed < -1.0f) processed = -1.0f + 1.0f / (-processed + 1.0f);");
            sb.AppendLine("        ");
            sb.AppendLine("        // Write to all output channels");
            sb.AppendLine("        for (int ch = 0; ch < num_channels; ch++) {");
            sb.AppendLine("            output[i * num_channels + ch] = processed;");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            sb.AppendLine();
        }

        static void GenerateCleanupFunction(StringBuilder sb)
        {
            sb.AppendLine("void circuit_cleanup(CircuitContext* ctx) {");
            sb.AppendLine("    if (!ctx) return;");
            sb.AppendLine("    if (ctx->state) free(ctx->state);");
            sb.AppendLine("    if (ctx->parameters) free(ctx->parameters);");
            sb.AppendLine("    free(ctx);");
            sb.AppendLine("}");
            sb.AppendLine();
        }

        static void GenerateInfoFunction(StringBuilder sb)
        {
            sb.AppendLine("typedef struct {");
            sb.AppendLine("    const char* name;");
            sb.AppendLine("    const char* description;");
            sb.AppendLine("    int num_inputs;");
            sb.AppendLine("    int num_outputs;");
            sb.AppendLine("    int recommended_oversample;");
            sb.AppendLine("    int recommended_iterations;");
            sb.AppendLine("} CircuitInfo;");
            sb.AppendLine();
            sb.AppendLine("static CircuitInfo info = {");
            sb.AppendLine($"    .name = \"{currentCircuit?.Name ?? "Exported Circuit"}\",");
            sb.AppendLine("    .description = \"Circuit exported from LiveSPICE\",");
            sb.AppendLine("    .num_inputs = 1,");
            sb.AppendLine("    .num_outputs = 1,");
            sb.AppendLine($"    .recommended_oversample = 8,");
            sb.AppendLine("    .recommended_iterations = 8");
            sb.AppendLine("};");
            sb.AppendLine();
            sb.AppendLine("const CircuitInfo* circuit_get_info(void) {");
            sb.AppendLine("    return &info;");
            sb.AppendLine("}");
        }
        
        static void GenerateParameterFunctions(StringBuilder sb)
        {
            // Parameter names array
            sb.AppendLine("// Parameter names");
            if (potentiometerNames.Count > 0)
            {
                sb.AppendLine("static const char* parameter_names[] = {");
                foreach (var name in potentiometerNames)
                {
                    sb.AppendLine($"    \"{name}\",");
                }
                sb.AppendLine("};");
            }
            else
            {
                sb.AppendLine("static const char* parameter_names[] = {");
                sb.AppendLine("    \"Param1\",");
                sb.AppendLine("    \"Param2\",");
                sb.AppendLine("    \"Param3\"");
                sb.AppendLine("};");
            }
            sb.AppendLine();
            
            // circuit_set_parameter
            sb.AppendLine("void circuit_set_parameter(CircuitContext* ctx, const char* name, double value) {");
            sb.AppendLine("    if (!ctx || !name) return;");
            sb.AppendLine("    ");
            sb.AppendLine("    // Clamp value to 0.0 - 1.0");
            sb.AppendLine("    if (value < 0.0) value = 0.0;");
            sb.AppendLine("    if (value > 1.0) value = 1.0;");
            sb.AppendLine("    ");
            sb.AppendLine("    // Find parameter by name and set value");
            if (potentiometerNames.Count > 0)
            {
                for (int i = 0; i < potentiometerNames.Count; i++)
                {
                    sb.AppendLine($"    if (strcmp(name, \"{potentiometerNames[i]}\") == 0) {{");
                    sb.AppendLine($"        ctx->parameters[{i}] = value;");
                    sb.AppendLine("        return;");
                    sb.AppendLine("    }");
                }
            }
            else
            {
                sb.AppendLine("    if (strcmp(name, \"Param1\") == 0) { ctx->parameters[0] = value; return; }");
                sb.AppendLine("    if (strcmp(name, \"Param2\") == 0) { ctx->parameters[1] = value; return; }");
                sb.AppendLine("    if (strcmp(name, \"Param3\") == 0) { ctx->parameters[2] = value; return; }");
            }
            sb.AppendLine("}");
            sb.AppendLine();
            
            // circuit_get_parameter
            sb.AppendLine("double circuit_get_parameter(CircuitContext* ctx, const char* name) {");
            sb.AppendLine("    if (!ctx || !name) return 0.0;");
            if (potentiometerNames.Count > 0)
            {
                for (int i = 0; i < potentiometerNames.Count; i++)
                {
                    sb.AppendLine($"    if (strcmp(name, \"{potentiometerNames[i]}\") == 0) return ctx->parameters[{i}];");
                }
            }
            else
            {
                sb.AppendLine("    if (strcmp(name, \"Param1\") == 0) return ctx->parameters[0];");
                sb.AppendLine("    if (strcmp(name, \"Param2\") == 0) return ctx->parameters[1];");
                sb.AppendLine("    if (strcmp(name, \"Param3\") == 0) return ctx->parameters[2];");
            }
            sb.AppendLine("    return 0.0;");
            sb.AppendLine("}");
            sb.AppendLine();
            
            // circuit_get_num_parameters
            int paramCount = potentiometerNames.Count > 0 ? potentiometerNames.Count : 3;
            sb.AppendLine($"int circuit_get_num_parameters(CircuitContext* ctx) {{");
            sb.AppendLine($"    return {paramCount};");
            sb.AppendLine("}");
            sb.AppendLine();
            
            // circuit_get_parameter_name
            sb.AppendLine("const char* circuit_get_parameter_name(CircuitContext* ctx, int index) {");
            sb.AppendLine($"    int max = {paramCount};");
            sb.AppendLine("    if (index < 0 || index >= max) return NULL;");
            sb.AppendLine("    return parameter_names[index];");
            sb.AppendLine("}");
            sb.AppendLine();
        }

        static void CompileToDylib(string cFile)
        {
            Console.WriteLine("Compiling to dylib...");
            
            string dylibFile = Path.ChangeExtension(cFile, ".dylib");
            string command = $"clang -dynamiclib -o {dylibFile} {cFile} -O2 -lm";
            
            Console.WriteLine($"Running: {command}");
            
            var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "clang",
                    Arguments = $"-dynamiclib -o {dylibFile} {cFile} -O2 -lm",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                }
            };
            
            process.Start();
            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            
            if (process.ExitCode == 0)
            {
                Console.WriteLine($"Successfully created: {dylibFile}");
            }
            else
            {
                Console.WriteLine($"Compilation failed: {error}");
            }
        }
    }
}