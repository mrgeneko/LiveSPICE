/**
 * Circuit API Header
 * Standard interface for LiveSPICE-compiled circuit dylibs
 */

#ifndef CIRCUIT_API_H
#define CIRCUIT_API_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/**
 * Circuit context - holds simulation state
 */
typedef struct CircuitContext {
    void* internal_state;      // Opaque pointer to simulation state
    int sample_rate;           // Sample rate in Hz
    int buffer_size;           // Buffer size in samples
    double timestep;           // Time step (1.0 / (sample_rate * oversample))
    int oversample;            // Oversampling factor
    double* parameters;        // Potentiometer/control values
    int num_parameters;        // Number of parameters
} CircuitContext;

/**
 * Initialize a circuit simulation
 * 
 * @param sample_rate Audio sample rate (e.g., 48000)
 * @param buffer_size Buffer size in samples (e.g., 256)
 * @param oversample Oversampling factor (e.g., 8)
 * @return Circuit context or NULL on error
 */
typedef CircuitContext* (*circuit_init_t)(int sample_rate, int buffer_size, int oversample);

/**
 * Process audio through the circuit
 * 
 * @param ctx Circuit context
 * @param input Input buffer (interleaved if multi-channel)
 * @param output Output buffer (interleaved if multi-channel)
 * @param num_samples Number of samples to process
 * @param num_channels Number of channels (1 for mono, 2 for stereo)
 */
typedef void (*circuit_process_t)(CircuitContext* ctx, 
                                   const float* input, 
                                   float* output, 
                                   int num_samples,
                                   int num_channels);

/**
 * Set a circuit parameter (e.g., potentiometer position)
 * 
 * @param ctx Circuit context
 * @param name Parameter name
 * @param value Parameter value (0.0 to 1.0 for pots)
 */
typedef void (*circuit_set_parameter_t)(CircuitContext* ctx, const char* name, double value);

/**
 * Get current parameter value
 * 
 * @param ctx Circuit context
 * @param name Parameter name
 * @return Current value
 */
typedef double (*circuit_get_parameter_t)(CircuitContext* ctx, const char* name);

/**
 * Get number of available parameters
 */
typedef int (*circuit_get_num_parameters_t)(CircuitContext* ctx);

/**
 * Get parameter name by index
 * 
 * @param ctx Circuit context
 * @param index Parameter index
 * @return Parameter name (static string, do not free)
 */
typedef const char* (*circuit_get_parameter_name_t)(CircuitContext* ctx, int index);

/**
 * Cleanup and free circuit resources
 * 
 * @param ctx Circuit context
 */
typedef void (*circuit_cleanup_t)(CircuitContext* ctx);

/**
 * Get circuit information
 */
typedef struct {
    const char* name;
    const char* description;
    int num_inputs;
    int num_outputs;
    int recommended_oversample;
    int recommended_iterations;
} CircuitInfo;

typedef const CircuitInfo* (*circuit_get_info_t)(void);

#ifdef __cplusplus
}
#endif

#endif // CIRCUIT_API_H