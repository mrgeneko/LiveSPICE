/**
 * Sample Circuit Implementation
 * A simple test circuit for demonstrating the C API
 * This is a basic tube-style distortion stage
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>
#include "circuit_api.h"

#define SAMPLE_CIRCUIT_NAME "Simple Tube Distortion"
#define SAMPLE_CIRCUIT_DESC "Basic tube emulation for testing"

// Circuit state
typedef struct {
    double gain;           // Input gain
    double threshold;      // Distortion threshold
    double makeup_gain;    // Output gain
    double last_output;    // For anti-aliasing (simple 1-pole)
    double sample_rate;
} SampleCircuitState;

// Parameter names
static const char* parameter_names[] = {
    "Gain",
    "Distortion", 
    "Volume"
};

static const int num_params = 3;

/**
 * Simple tube saturation function
 */
static inline double tube_saturate(double x, double threshold) {
    // Asymmetric tube distortion
    if (x > threshold) {
        return threshold + (x - threshold) / (1.0 + (x - threshold) / threshold);
    } else if (x < -threshold) {
        return -threshold + (x + threshold) / (1.0 - (x + threshold) / threshold);
    }
    return x;
}

/**
 * Initialize circuit
 */
CircuitContext* circuit_init(int sample_rate, int buffer_size, int oversample) {
    CircuitContext* ctx = (CircuitContext*)malloc(sizeof(CircuitContext));
    if (!ctx) return NULL;
    
    SampleCircuitState* state = (SampleCircuitState*)malloc(sizeof(SampleCircuitState));
    if (!state) {
        free(ctx);
        return NULL;
    }
    
    // Initialize state
    state->gain = 1.0;
    state->threshold = 0.5;
    state->makeup_gain = 0.7;
    state->last_output = 0.0;
    state->sample_rate = sample_rate;
    
    // Setup context
    ctx->internal_state = state;
    ctx->sample_rate = sample_rate;
    ctx->buffer_size = buffer_size;
    ctx->timestep = 1.0 / (sample_rate * oversample);
    ctx->oversample = oversample;
    ctx->num_parameters = num_params;
    ctx->parameters = (double*)malloc(sizeof(double) * num_params);
    
    // Default parameter values (0.5 = midpoint)
    ctx->parameters[0] = 0.5;  // Gain
    ctx->parameters[1] = 0.5;  // Distortion
    ctx->parameters[2] = 0.7;  // Volume
    
    printf("Circuit initialized: %s\n", SAMPLE_CIRCUIT_NAME);
    printf("  Sample rate: %d Hz\n", sample_rate);
    printf("  Buffer size: %d samples\n", buffer_size);
    printf("  Oversample: %dx\n", oversample);
    
    return ctx;
}

/**
 * Process audio
 */
void circuit_process(CircuitContext* ctx, 
                     const float* input, 
                     float* output, 
                     int num_samples,
                     int num_channels) {
    if (!ctx || !ctx->internal_state) return;
    
    SampleCircuitState* state = (SampleCircuitState*)ctx->internal_state;
    
    // Map parameters to circuit values
    double gain = 0.5 + ctx->parameters[0] * 9.5;        // 0.5 to 10.0
    double distortion = 0.1 + ctx->parameters[1] * 0.9;  // 0.1 to 1.0
    double volume = ctx->parameters[2];                   // 0.0 to 1.0
    
    state->gain = gain;
    state->threshold = distortion;
    state->makeup_gain = volume;
    
    // Simple oversampling (linear interpolation)
    int oversample = ctx->oversample;
    
    for (int i = 0; i < num_samples; i++) {
        float sample = input[i * num_channels];  // Use first channel
        
        // Oversample loop
        float processed = 0.0f;
        for (int os = 0; os < oversample; os++) {
            // Apply gain
            double amplified = sample * state->gain;
            
            // Apply distortion
            double distorted = tube_saturate(amplified, state->threshold);
            
            // Apply makeup gain
            processed = distorted * state->makeup_gain;
            
            // Simple anti-aliasing (1-pole lowpass)
            processed = 0.5f * processed + 0.5f * state->last_output;
            state->last_output = processed;
        }
        
        // Average oversampled result
        processed /= oversample;
        
        // Write to all output channels
        for (int ch = 0; ch < num_channels; ch++) {
            output[i * num_channels + ch] = processed;
        }
    }
}

/**
 * Set parameter
 */
void circuit_set_parameter(CircuitContext* ctx, const char* name, double value) {
    if (!ctx) return;
    
    // Clamp value to 0.0 - 1.0
    if (value < 0.0) value = 0.0;
    if (value > 1.0) value = 1.0;
    
    if (strcmp(name, "Gain") == 0) {
        ctx->parameters[0] = value;
    } else if (strcmp(name, "Distortion") == 0) {
        ctx->parameters[1] = value;
    } else if (strcmp(name, "Volume") == 0) {
        ctx->parameters[2] = value;
    }
}

/**
 * Get parameter
 */
double circuit_get_parameter(CircuitContext* ctx, const char* name) {
    if (!ctx) return 0.0;
    
    if (strcmp(name, "Gain") == 0) {
        return ctx->parameters[0];
    } else if (strcmp(name, "Distortion") == 0) {
        return ctx->parameters[1];
    } else if (strcmp(name, "Volume") == 0) {
        return ctx->parameters[2];
    }
    
    return 0.0;
}

/**
 * Get number of parameters
 */
int circuit_get_num_parameters(CircuitContext* ctx) {
    return num_params;
}

/**
 * Get parameter name
 */
const char* circuit_get_parameter_name(CircuitContext* ctx, int index) {
    if (index >= 0 && index < num_params) {
        return parameter_names[index];
    }
    return NULL;
}

/**
 * Cleanup
 */
void circuit_cleanup(CircuitContext* ctx) {
    if (!ctx) return;
    
    if (ctx->internal_state) {
        free(ctx->internal_state);
    }
    if (ctx->parameters) {
        free(ctx->parameters);
    }
    free(ctx);
    
    printf("Circuit cleaned up\n");
}

/**
 * Get circuit info
 */
static CircuitInfo info = {
    .name = SAMPLE_CIRCUIT_NAME,
    .description = SAMPLE_CIRCUIT_DESC,
    .num_inputs = 1,
    .num_outputs = 1,
    .recommended_oversample = 8,
    .recommended_iterations = 8
};

const CircuitInfo* circuit_get_info(void) {
    return &info;
}