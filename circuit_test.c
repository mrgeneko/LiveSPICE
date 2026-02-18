/**
 * Circuit Test CLI Tool
 * Loads a circuit dylib and processes audio files
 * 
 * Usage: ./circuit_test --input input.wav --circuit circuit.dylib --output output.wav [options]
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <math.h>
#include <dlfcn.h>
#include <sndfile.h>
#include <getopt.h>
#include "circuit_api.h"

#define DEFAULT_SAMPLE_RATE 48000
#define DEFAULT_BUFFER_SIZE 256
#define DEFAULT_OVERSAMPLE 8

typedef struct {
    char* input_file;
    char* circuit_file;
    char* output_file;
    int sample_rate;
    int buffer_size;
    int oversample;
    int measure_latency;
    int verbose;
    char** param_values;
    int num_params;
} TestConfig;

// Function pointers for dynamically loaded functions
circuit_init_t circuit_init = NULL;
circuit_process_t circuit_process = NULL;
circuit_set_parameter_t circuit_set_parameter = NULL;
circuit_get_parameter_t circuit_get_parameter = NULL;
circuit_get_num_parameters_t circuit_get_num_parameters = NULL;
circuit_get_parameter_name_t circuit_get_parameter_name = NULL;
circuit_cleanup_t circuit_cleanup = NULL;
circuit_get_info_t circuit_get_info = NULL;

void print_usage(const char* program) {
    printf("Usage: %s [options]\n\n", program);
    printf("Options:\n");
    printf("  -i, --input FILE          Input WAV file\n");
    printf("  -c, --circuit FILE        Circuit dylib file\n");
    printf("  -o, --output FILE         Output WAV file\n");
    printf("  -r, --sample-rate RATE    Sample rate (default: %d)\n", DEFAULT_SAMPLE_RATE);
    printf("  -b, --buffer-size SIZE    Buffer size in samples (default: %d)\n", DEFAULT_BUFFER_SIZE);
    printf("  -v, --oversample N        Oversampling factor (default: %d)\n", DEFAULT_OVERSAMPLE);
    printf("  -p, --param NAME=VALUE    Set parameter (e.g., Gain=0.7)\n");
    printf("  -m, --measure-latency     Measure processing latency\n");
    printf("  -V, --verbose             Verbose output\n");
    printf("  -h, --help                Show this help\n");
}

int parse_args(int argc, char* argv[], TestConfig* config) {
    // Set defaults
    config->sample_rate = DEFAULT_SAMPLE_RATE;
    config->buffer_size = DEFAULT_BUFFER_SIZE;
    config->oversample = DEFAULT_OVERSAMPLE;
    config->measure_latency = 0;
    config->verbose = 0;
    config->param_values = NULL;
    config->num_params = 0;
    
    static struct option long_options[] = {
        {"input", required_argument, 0, 'i'},
        {"circuit", required_argument, 0, 'c'},
        {"output", required_argument, 0, 'o'},
        {"sample-rate", required_argument, 0, 'r'},
        {"buffer-size", required_argument, 0, 'b'},
        {"oversample", required_argument, 0, 'v'},
        {"param", required_argument, 0, 'p'},
        {"measure-latency", no_argument, 0, 'm'},
        {"verbose", no_argument, 0, 'V'},
        {"help", no_argument, 0, 'h'},
        {0, 0, 0, 0}
    };
    
    int option_index = 0;
    int c;
    
    while ((c = getopt_long(argc, argv, "i:c:o:r:b:v:p:mVh", long_options, &option_index)) != -1) {
        switch (c) {
            case 'i':
                config->input_file = strdup(optarg);
                break;
            case 'c':
                config->circuit_file = strdup(optarg);
                break;
            case 'o':
                config->output_file = strdup(optarg);
                break;
            case 'r':
                config->sample_rate = atoi(optarg);
                break;
            case 'b':
                config->buffer_size = atoi(optarg);
                break;
            case 'v':
                config->oversample = atoi(optarg);
                break;
            case 'p': {
                // Parse NAME=VALUE
                config->param_values = realloc(config->param_values, 
                                               (config->num_params + 1) * sizeof(char*));
                config->param_values[config->num_params] = strdup(optarg);
                config->num_params++;
                break;
            }
            case 'm':
                config->measure_latency = 1;
                break;
            case 'V':
                config->verbose = 1;
                break;
            case 'h':
                print_usage(argv[0]);
                exit(0);
            case '?':
                return -1;
        }
    }
    
    // Validate required args
    if (!config->input_file || !config->circuit_file || !config->output_file) {
        fprintf(stderr, "Error: Input, circuit, and output files are required\n");
        return -1;
    }
    
    return 0;
}

int load_circuit(const char* circuit_file) {
    void* handle = dlopen(circuit_file, RTLD_LAZY);
    if (!handle) {
        fprintf(stderr, "Error loading circuit: %s\n", dlerror());
        return -1;
    }
    
    // Load function pointers
    circuit_init = (circuit_init_t)dlsym(handle, "circuit_init");
    circuit_process = (circuit_process_t)dlsym(handle, "circuit_process");
    circuit_set_parameter = (circuit_set_parameter_t)dlsym(handle, "circuit_set_parameter");
    circuit_get_parameter = (circuit_get_parameter_t)dlsym(handle, "circuit_get_parameter");
    circuit_get_num_parameters = (circuit_get_num_parameters_t)dlsym(handle, "circuit_get_num_parameters");
    circuit_get_parameter_name = (circuit_get_parameter_name_t)dlsym(handle, "circuit_get_parameter_name");
    circuit_cleanup = (circuit_cleanup_t)dlsym(handle, "circuit_cleanup");
    circuit_get_info = (circuit_get_info_t)dlsym(handle, "circuit_get_info");
    
    // Check required functions
    if (!circuit_init || !circuit_process || !circuit_cleanup) {
        fprintf(stderr, "Error: Circuit is missing required functions\n");
        return -1;
    }
    
    return 0;
}

int process_audio(TestConfig* config) {
    SF_INFO sfinfo_in, sfinfo_out;
    SNDFILE* infile = NULL;
    SNDFILE* outfile = NULL;
    CircuitContext* ctx = NULL;
    float* input_buffer = NULL;
    float* output_buffer = NULL;
    int result = -1;
    
    // Open input file
    memset(&sfinfo_in, 0, sizeof(sfinfo_in));
    infile = sf_open(config->input_file, SFM_READ, &sfinfo_in);
    if (!infile) {
        fprintf(stderr, "Error opening input file: %s\n", sf_strerror(NULL));
        goto cleanup;
    }
    
    printf("Input file: %s\n", config->input_file);
    printf("  Sample rate: %d Hz\n", sfinfo_in.samplerate);
    printf("  Channels: %d\n", sfinfo_in.channels);
    printf("  Frames: %ld\n", (long)sfinfo_in.frames);
    
    // Setup output file
    memset(&sfinfo_out, 0, sizeof(sfinfo_out));
    sfinfo_out.samplerate = sfinfo_in.samplerate;
    sfinfo_out.channels = sfinfo_in.channels;
    sfinfo_out.format = sfinfo_in.format;
    
    outfile = sf_open(config->output_file, SFM_WRITE, &sfinfo_out);
    if (!outfile) {
        fprintf(stderr, "Error opening output file: %s\n", sf_strerror(NULL));
        goto cleanup;
    }
    
    // Initialize circuit
    ctx = circuit_init(sfinfo_in.samplerate, config->buffer_size, config->oversample);
    if (!ctx) {
        fprintf(stderr, "Error initializing circuit\n");
        goto cleanup;
    }
    
    // Print circuit info if available
    if (circuit_get_info) {
        const CircuitInfo* info = circuit_get_info();
        printf("\nCircuit: %s\n", info->name);
        printf("  Description: %s\n", info->description);
        printf("  Inputs: %d, Outputs: %d\n", info->num_inputs, info->num_outputs);
    }
    
    // Set parameters
    if (config->num_params > 0 && circuit_set_parameter) {
        printf("\nSetting parameters:\n");
        for (int i = 0; i < config->num_params; i++) {
            char* param = strdup(config->param_values[i]);
            char* value_str = strchr(param, '=');
            if (value_str) {
                *value_str = '\0';
                value_str++;
                double value = atof(value_str);
                circuit_set_parameter(ctx, param, value);
                printf("  %s = %.3f\n", param, value);
            }
            free(param);
        }
    }
    
    // Allocate buffers
    int buffer_samples = config->buffer_size * sfinfo_in.channels;
    input_buffer = (float*)malloc(buffer_samples * sizeof(float));
    output_buffer = (float*)malloc(buffer_samples * sizeof(float));
    
    if (!input_buffer || !output_buffer) {
        fprintf(stderr, "Error allocating buffers\n");
        goto cleanup;
    }
    
    // Process audio
    printf("\nProcessing audio...\n");
    
    sf_count_t total_frames = 0;
    sf_count_t frames_read;
    clock_t total_process_time = 0;
    int buffer_count = 0;
    
    while ((frames_read = sf_read_float(infile, input_buffer, buffer_samples)) > 0) {
        int frames_to_process = frames_read / sfinfo_in.channels;
        
        // Measure processing time
        clock_t start = clock();
        
        circuit_process(ctx, input_buffer, output_buffer, frames_to_process, sfinfo_in.channels);
        
        clock_t end = clock();
        total_process_time += (end - start);
        buffer_count++;
        
        // Write output
        sf_write_float(outfile, output_buffer, frames_read);
        total_frames += frames_read;
        
        if (config->verbose && buffer_count % 100 == 0) {
            printf("  Processed %ld frames...\n", (long)total_frames / sfinfo_in.channels);
        }
    }
    
    // Calculate statistics
    double total_time = ((double)total_process_time) / CLOCKS_PER_SEC;
    double audio_duration = (double)total_frames / sfinfo_in.samplerate / sfinfo_in.channels;
    double real_time_ratio = audio_duration / total_time;
    double load_percent = (total_time / audio_duration) * 100.0;
    double latency_ms = (double)config->buffer_size / sfinfo_in.samplerate * 1000.0;
    
    printf("\nProcessing complete!\n");
    printf("  Output file: %s\n", config->output_file);
    printf("  Total frames: %ld\n", (long)total_frames / sfinfo_in.channels);
    printf("  Audio duration: %.3f seconds\n", audio_duration);
    printf("  Processing time: %.3f seconds\n", total_time);
    printf("  Real-time ratio: %.2fx\n", real_time_ratio);
    printf("  DSP Load: %.1f%%\n", load_percent);
    printf("  Latency: %.2f ms (%d samples @ %d Hz)\n", 
           latency_ms, config->buffer_size, sfinfo_in.samplerate);
    printf("  Buffer count: %d\n", buffer_count);
    
    if (config->measure_latency) {
        printf("\nLatency Analysis:\n");
        printf("  Buffer latency: %.2f ms\n", latency_ms);
        printf("  Recommended for real-time: < 10 ms\n");
        if (latency_ms > 10.0) {
            printf("  ⚠️  WARNING: Latency exceeds recommended threshold\n");
        }
    }
    
    result = 0;
    
cleanup:
    if (ctx) circuit_cleanup(ctx);
    if (infile) sf_close(infile);
    if (outfile) sf_close(outfile);
    free(input_buffer);
    free(output_buffer);
    
    return result;
}

int main(int argc, char* argv[]) {
    TestConfig config;
    
    printf("Circuit Test Tool v1.0\n");
    printf("======================\n\n");
    
    if (parse_args(argc, argv, &config) != 0) {
        print_usage(argv[0]);
        return 1;
    }
    
    printf("Configuration:\n");
    printf("  Input: %s\n", config.input_file);
    printf("  Circuit: %s\n", config.circuit_file);
    printf("  Output: %s\n", config.output_file);
    printf("  Sample rate: %d Hz\n", config.sample_rate);
    printf("  Buffer size: %d samples\n", config.buffer_size);
    printf("  Oversample: %dx\n", config.oversample);
    printf("\n");
    
    // Load circuit
    if (load_circuit(config.circuit_file) != 0) {
        return 1;
    }
    
    // Process audio
    int result = process_audio(&config);
    
    // Cleanup
    free(config.input_file);
    free(config.circuit_file);
    free(config.output_file);
    for (int i = 0; i < config.num_params; i++) {
        free(config.param_values[i]);
    }
    free(config.param_values);
    
    return result;
}