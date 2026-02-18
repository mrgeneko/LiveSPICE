# Circuit Test Tool Makefile

CC = clang
CFLAGS = -Wall -O2 -std=c11
LDFLAGS = -lsndfile -ldl

# Detect platform
UNAME_S := $(shell uname -s)
ifeq ($(UNAME_S),Darwin)
    # macOS
    DYLIB_EXT = dylib
    DYLIB_FLAGS = -dynamiclib -install_name @rpath/
else
    # Linux
    DYLIB_EXT = so
    DYLIB_FLAGS = -shared -fPIC
endif

TARGETS = circuit_test sample_circuit.$(DYLIB_EXT)

all: $(TARGETS)

# Build the test CLI tool
circuit_test: circuit_test.c circuit_api.h
	$(CC) $(CFLAGS) -o $@ circuit_test.c $(LDFLAGS)

# Build the sample circuit dylib
sample_circuit.$(DYLIB_EXT): sample_circuit.c circuit_api.h
	$(CC) $(CFLAGS) $(DYLIB_FLAGS) -o $@ sample_circuit.c

# Clean build artifacts
clean:
	rm -f circuit_test sample_circuit.$(DYLIB_EXT) *.o

# Run a test
test: all
	@echo "Creating test audio file..."
	@# Create a simple sine wave test file using sox (if available)
	@if command -v sox >/dev/null 2>&1; then \
		sox -n -r 48000 -b 16 test_input.wav synth 3 sine 440; \
		echo "Running circuit test..."; \
		./circuit_test -i test_input.wav -c sample_circuit.$(DYLIB_EXT) -o test_output.wav -V; \
	else \
		echo "sox not found. Please install sox to generate test audio."; \
		echo "Or provide your own WAV file: ./circuit_test -i input.wav -c sample_circuit.$(DYLIB_EXT) -o output.wav"; \
	fi

# Install dependencies (macOS)
install-deps-macos:
	brew install libsndfile

# Install dependencies (Ubuntu/Debian)
install-deps-linux:
	sudo apt-get update
	sudo apt-get install -y libsndfile1-dev

.PHONY: all clean test install-deps-macos install-deps-linux