#define MINIAUDIO_IMPLEMENTATION
#define MA_NO_DECODING
#define MA_NO_ENCODING
#define MA_NO_GENERATION
#define AUDIOCAPTURE_BUILD
#include "miniaudio.h"
#include "audiocapture.h"

#include <stdatomic.h>
#include <string.h>
#include <stdio.h>

/*
 * One second of headroom at the requested format. Long enough that a UI thread
 * blocked briefly -- a layout pass, a GC pause -- does not lose audio, short
 * enough that a caller who stops draining entirely is told quickly rather than
 * accumulating minutes of stale sound.
 */
#define AUDIOCAPTURE_BUFFER_SECONDS 1

struct audiocapture_device {
    ma_device device;
    ma_pcm_rb  ring;
    int        channels;
    int        started;
    _Atomic unsigned long long overruns;
    _Atomic float peak;
};

/* One context for the process: opening a device per call would re-enumerate. */
static ma_context g_context;
static int        g_context_ready = 0;
static ma_device_info * g_capture_devices = NULL;
static ma_uint32        g_capture_count = 0;

static int ensure_context(void) {
    if (g_context_ready) return 1;
    if (ma_context_init(NULL, 0, NULL, &g_context) != MA_SUCCESS) return 0;
    g_context_ready = 1;
    return 1;
}

unsigned int audiocapture_version(void) {
    return (MA_VERSION_MAJOR << 16) | (MA_VERSION_MINOR << 8) | MA_VERSION_REVISION;
}

const char * audiocapture_backend(void) {
    if (!ensure_context()) return "none";
    return ma_get_backend_name(g_context.backend);
}

int audiocapture_refresh_devices(void) {
    if (!ensure_context()) return 0;

    ma_device_info * playback = NULL;
    ma_uint32 playback_count = 0;
    if (ma_context_get_devices(&g_context, &playback, &playback_count,
                               &g_capture_devices, &g_capture_count) != MA_SUCCESS) {
        g_capture_devices = NULL;
        g_capture_count = 0;
        return 0;
    }
    return (int)g_capture_count;
}

int audiocapture_device_count(void) {
    return (int)g_capture_count;
}

const char * audiocapture_device_name(int index) {
    if (g_capture_devices == NULL || index < 0 || (ma_uint32)index >= g_capture_count) {
        return "";
    }
    return g_capture_devices[index].name;
}

int audiocapture_device_is_default(int index) {
    if (g_capture_devices == NULL || index < 0 || (ma_uint32)index >= g_capture_count) {
        return 0;
    }
    return g_capture_devices[index].isDefault ? 1 : 0;
}

/*
 * Runs on miniaudio's audio thread. Everything here is lock-free and
 * allocation-free by necessity: blocking this callback is what produces the
 * crackle users blame on the model.
 */
static void on_frames(ma_device * device, void * output, const void * input, ma_uint32 frame_count) {
    (void)output;
    audiocapture_device * self = (audiocapture_device *)device->pUserData;
    if (self == NULL || input == NULL) return;

    const float * samples = (const float *)input;

    float peak = 0.0f;
    const ma_uint32 total = frame_count * (ma_uint32)self->channels;
    for (ma_uint32 i = 0; i < total; i++) {
        const float magnitude = samples[i] < 0.0f ? -samples[i] : samples[i];
        if (magnitude > peak) peak = magnitude;
    }
    float seen = atomic_load(&self->peak);
    if (peak > seen) atomic_store(&self->peak, peak);

    ma_uint32 remaining = frame_count;
    const float * cursor = samples;
    while (remaining > 0) {
        ma_uint32 writable = remaining;
        void * region = NULL;
        if (ma_pcm_rb_acquire_write(&self->ring, &writable, &region) != MA_SUCCESS || writable == 0) {
            atomic_fetch_add(&self->overruns, (unsigned long long)remaining);
            return;
        }
        memcpy(region, cursor, (size_t)writable * (size_t)self->channels * sizeof(float));
        ma_pcm_rb_commit_write(&self->ring, writable);
        cursor    += (size_t)writable * (size_t)self->channels;
        remaining -= writable;
    }
}

audiocapture_device * audiocapture_open(
    int device_index, int sample_rate, int channels, char * err, size_t err_len) {

    if (err != NULL && err_len > 0) err[0] = '\0';

    if (sample_rate <= 0 || channels <= 0) {
        if (err != NULL && err_len > 0) snprintf(err, err_len, "invalid format");
        return NULL;
    }
    if (!ensure_context()) {
        if (err != NULL && err_len > 0) snprintf(err, err_len, "no audio backend available");
        return NULL;
    }
    if (device_index >= 0 && (g_capture_devices == NULL || (ma_uint32)device_index >= g_capture_count)) {
        if (err != NULL && err_len > 0) snprintf(err, err_len, "device index out of range");
        return NULL;
    }

    audiocapture_device * self = (audiocapture_device *)ma_malloc(sizeof(*self), NULL);
    if (self == NULL) {
        if (err != NULL && err_len > 0) snprintf(err, err_len, "out of memory");
        return NULL;
    }
    memset(self, 0, sizeof(*self));
    self->channels = channels;

    const ma_uint32 ring_frames = (ma_uint32)(sample_rate * AUDIOCAPTURE_BUFFER_SECONDS);
    if (ma_pcm_rb_init(ma_format_f32, (ma_uint32)channels, ring_frames, NULL, NULL, &self->ring) != MA_SUCCESS) {
        if (err != NULL && err_len > 0) snprintf(err, err_len, "could not allocate the capture buffer");
        ma_free(self, NULL);
        return NULL;
    }

    ma_device_config config = ma_device_config_init(ma_device_type_capture);
    config.capture.format   = ma_format_f32;   /* miniaudio converts for us */
    config.capture.channels = (ma_uint32)channels;
    config.sampleRate       = (ma_uint32)sample_rate;
    config.dataCallback     = on_frames;
    config.pUserData        = self;
    if (device_index >= 0) {
        config.capture.pDeviceID = &g_capture_devices[device_index].id;
    }

    const ma_result result = ma_device_init(&g_context, &config, &self->device);
    if (result != MA_SUCCESS) {
        if (err != NULL && err_len > 0) {
            snprintf(err, err_len, "could not open the device: %s", ma_result_description(result));
        }
        ma_pcm_rb_uninit(&self->ring);
        ma_free(self, NULL);
        return NULL;
    }
    return self;
}

int audiocapture_start(audiocapture_device * self) {
    if (self == NULL) return 0;
    if (self->started) return 1;
    if (ma_device_start(&self->device) != MA_SUCCESS) return 0;
    self->started = 1;
    return 1;
}

int audiocapture_stop(audiocapture_device * self) {
    if (self == NULL) return 0;
    if (!self->started) return 1;
    if (ma_device_stop(&self->device) != MA_SUCCESS) return 0;
    self->started = 0;
    return 1;
}

void audiocapture_close(audiocapture_device * self) {
    if (self == NULL) return;
    /* uninit stops the device and joins its thread, so the callback cannot be
       running by the time the ring buffer goes away. */
    ma_device_uninit(&self->device);
    ma_pcm_rb_uninit(&self->ring);
    ma_free(self, NULL);
}

size_t audiocapture_read(audiocapture_device * self, float * out, size_t frame_count) {
    if (self == NULL || out == NULL || frame_count == 0) return 0;

    size_t written = 0;
    while (written < frame_count) {
        ma_uint32 readable = (ma_uint32)(frame_count - written);
        void * region = NULL;
        if (ma_pcm_rb_acquire_read(&self->ring, &readable, &region) != MA_SUCCESS || readable == 0) {
            break;
        }
        memcpy(out + written * (size_t)self->channels, region,
               (size_t)readable * (size_t)self->channels * sizeof(float));
        ma_pcm_rb_commit_read(&self->ring, readable);
        written += readable;
    }
    return written;
}

unsigned long long audiocapture_overruns(audiocapture_device * self) {
    if (self == NULL) return 0;
    return atomic_exchange(&self->overruns, 0ULL);
}

float audiocapture_peak(audiocapture_device * self) {
    if (self == NULL) return 0.0f;
    return atomic_exchange(&self->peak, 0.0f);
}
