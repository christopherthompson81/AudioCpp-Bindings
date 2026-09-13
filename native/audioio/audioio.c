#define MINIAUDIO_IMPLEMENTATION
#define MA_NO_DECODING
#define MA_NO_ENCODING
#define MA_NO_GENERATION
#define AUDIOIO_BUILD
#include "miniaudio.h"
#include "audioio.h"

#include <stdatomic.h>
#include <string.h>
#include <stdio.h>

/*
 * One second of headroom at the requested format. Long enough that a UI thread
 * blocked briefly -- a layout pass, a GC pause -- does not lose audio, short
 * enough that a caller who stops draining entirely is told quickly rather than
 * accumulating minutes of stale sound.
 */
#define AUDIOIO_BUFFER_SECONDS 1

struct audioio_device {
    ma_device device;
    ma_pcm_rb  ring;
    int        channels;
    int        started;
    _Atomic unsigned long long overruns;
    _Atomic float peak;
};

/*
 * One context for the process: opening a device per call would re-enumerate.
 *
 * The mutex guards both the lazy init and the device list, because a caller is
 * free to enumerate on one thread while opening on another, and the list is
 * memory the context owns and rewrites on every refresh.
 */
static ma_context g_context;
static int        g_context_ready = 0;
static ma_mutex   g_lock;
static int        g_lock_ready = 0;
static ma_device_info * g_capture_devices = NULL;
static ma_uint32        g_capture_count = 0;

static void lock_devices(void) {
    if (g_lock_ready) ma_mutex_lock(&g_lock);
}

static void unlock_devices(void) {
    if (g_lock_ready) ma_mutex_unlock(&g_lock);
}

static int ensure_context(void) {
    if (g_context_ready) return 1;
    if (ma_context_init(NULL, 0, NULL, &g_context) != MA_SUCCESS) return 0;
    if (ma_mutex_init(&g_lock) == MA_SUCCESS) g_lock_ready = 1;
    g_context_ready = 1;
    return 1;
}

unsigned int audioio_version(void) {
    return (MA_VERSION_MAJOR << 16) | (MA_VERSION_MINOR << 8) | MA_VERSION_REVISION;
}

const char * audioio_backend(void) {
    if (!ensure_context()) return "none";
    return ma_get_backend_name(g_context.backend);
}

int audioio_refresh_devices(void) {
    if (!ensure_context()) return 0;

    lock_devices();
    ma_device_info * playback = NULL;
    ma_uint32 playback_count = 0;
    if (ma_context_get_devices(&g_context, &playback, &playback_count,
                               &g_capture_devices, &g_capture_count) != MA_SUCCESS) {
        g_capture_devices = NULL;
        g_capture_count = 0;
    }
    const int count = (int)g_capture_count;
    unlock_devices();
    return count;
}

int audioio_device_count(void) {
    lock_devices();
    const int count = (int)g_capture_count;
    unlock_devices();
    return count;
}

const char * audioio_device_name(int index) {
    lock_devices();
    const char * name = "";
    if (g_capture_devices != NULL && index >= 0 && (ma_uint32)index < g_capture_count) {
        name = g_capture_devices[index].name;
    }
    unlock_devices();
    return name;
}

int audioio_device_is_default(int index) {
    lock_devices();
    int is_default = 0;
    if (g_capture_devices != NULL && index >= 0 && (ma_uint32)index < g_capture_count) {
        is_default = g_capture_devices[index].isDefault ? 1 : 0;
    }
    unlock_devices();
    return is_default;
}

/*
 * Runs on miniaudio's audio thread. Everything here is lock-free and
 * allocation-free by necessity: blocking this callback is what produces the
 * crackle users blame on the model.
 */
static void on_frames(ma_device * device, void * output, const void * input, ma_uint32 frame_count) {
    (void)output;
    audioio_device * self = (audioio_device *)device->pUserData;
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

audioio_device * audioio_open(
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
    /* Copy the id rather than pointing into the shared list: a refresh on
       another thread rewrites that memory, and miniaudio would read it after we
       had let go of the lock. */
    ma_device_id chosen_id;
    int have_id = 0;
    if (device_index >= 0) {
        lock_devices();
        if (g_capture_devices == NULL || (ma_uint32)device_index >= g_capture_count) {
            unlock_devices();
            if (err != NULL && err_len > 0) snprintf(err, err_len, "device index out of range");
            return NULL;
        }
        chosen_id = g_capture_devices[device_index].id;
        have_id = 1;
        unlock_devices();
    }

    audioio_device * self = (audioio_device *)ma_malloc(sizeof(*self), NULL);
    if (self == NULL) {
        if (err != NULL && err_len > 0) snprintf(err, err_len, "out of memory");
        return NULL;
    }
    memset(self, 0, sizeof(*self));
    self->channels = channels;

    const ma_uint32 ring_frames = (ma_uint32)(sample_rate * AUDIOIO_BUFFER_SECONDS);
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
    if (have_id) {
        config.capture.pDeviceID = &chosen_id;
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

int audioio_start(audioio_device * self) {
    if (self == NULL) return 0;
    if (self->started) return 1;
    if (ma_device_start(&self->device) != MA_SUCCESS) return 0;
    self->started = 1;
    return 1;
}

int audioio_stop(audioio_device * self) {
    if (self == NULL) return 0;
    if (!self->started) return 1;
    if (ma_device_stop(&self->device) != MA_SUCCESS) return 0;
    self->started = 0;
    return 1;
}

void audioio_close(audioio_device * self) {
    if (self == NULL) return;
    /* uninit stops the device and joins its thread, so the callback cannot be
       running by the time the ring buffer goes away. */
    ma_device_uninit(&self->device);
    ma_pcm_rb_uninit(&self->ring);
    ma_free(self, NULL);
}

size_t audioio_read(audioio_device * self, float * out, size_t frame_count) {
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

unsigned long long audioio_overruns(audioio_device * self) {
    if (self == NULL) return 0;
    return atomic_exchange(&self->overruns, 0ULL);
}

float audioio_peak(audioio_device * self) {
    if (self == NULL) return 0.0f;
    return atomic_exchange(&self->peak, 0.0f);
}

/* ------------------------------------------------------------------ */
/* Playback                                                            */
/* ------------------------------------------------------------------ */

struct audioio_player {
    ma_device device;
    float *   samples;       /* owned copy; see the header for why */
    size_t    frames;
    int       channels;
    _Atomic size_t position; /* frames, written by the audio thread */
    _Atomic int    playing;
};

/*
 * Runs on miniaudio's audio thread. Reads the clip from the current position
 * and advances it; stops at the end rather than looping, which is what a
 * preview should do.
 */
static void on_playback(ma_device * device, void * output, const void * input, ma_uint32 frame_count) {
    (void)input;
    audioio_player * self = (audioio_player *)device->pUserData;
    float * out = (float *)output;

    const size_t channels = (size_t)self->channels;
    memset(out, 0, (size_t)frame_count * channels * sizeof(float));
    if (!atomic_load(&self->playing)) return;

    size_t position = atomic_load(&self->position);
    if (position >= self->frames) {
        atomic_store(&self->playing, 0);
        return;
    }

    size_t available = self->frames - position;
    size_t take = frame_count < available ? frame_count : available;
    memcpy(out, self->samples + position * channels, take * channels * sizeof(float));

    position += take;
    atomic_store(&self->position, position);
    if (position >= self->frames) atomic_store(&self->playing, 0);
}

audioio_player * audioio_player_open(
    const float * samples, size_t frame_count,
    int sample_rate, int channels, char * err, size_t err_len) {

    if (err != NULL && err_len > 0) err[0] = '\0';

    if (samples == NULL || frame_count == 0 || sample_rate <= 0 || channels <= 0) {
        if (err != NULL && err_len > 0) snprintf(err, err_len, "nothing to play");
        return NULL;
    }
    if (!ensure_context()) {
        if (err != NULL && err_len > 0) snprintf(err, err_len, "no audio backend available");
        return NULL;
    }

    audioio_player * self = (audioio_player *)ma_malloc(sizeof(*self), NULL);
    if (self == NULL) {
        if (err != NULL && err_len > 0) snprintf(err, err_len, "out of memory");
        return NULL;
    }
    memset(self, 0, sizeof(*self));
    self->frames = frame_count;
    self->channels = channels;

    const size_t bytes = frame_count * (size_t)channels * sizeof(float);
    self->samples = (float *)ma_malloc(bytes, NULL);
    if (self->samples == NULL) {
        if (err != NULL && err_len > 0) snprintf(err, err_len, "clip too large to copy");
        ma_free(self, NULL);
        return NULL;
    }
    memcpy(self->samples, samples, bytes);

    ma_device_config config = ma_device_config_init(ma_device_type_playback);
    config.playback.format   = ma_format_f32;
    config.playback.channels = (ma_uint32)channels;
    config.sampleRate        = (ma_uint32)sample_rate;
    config.dataCallback      = on_playback;
    config.pUserData         = self;

    const ma_result result = ma_device_init(&g_context, &config, &self->device);
    if (result != MA_SUCCESS) {
        if (err != NULL && err_len > 0) {
            snprintf(err, err_len, "could not open the output: %s", ma_result_description(result));
        }
        ma_free(self->samples, NULL);
        ma_free(self, NULL);
        return NULL;
    }

    /* Started once and left running: starting a device costs tens of
       milliseconds, which is audible as a clip at the head of every play. The
       callback outputs silence while paused. */
    if (ma_device_start(&self->device) != MA_SUCCESS) {
        if (err != NULL && err_len > 0) snprintf(err, err_len, "could not start the output");
        ma_device_uninit(&self->device);
        ma_free(self->samples, NULL);
        ma_free(self, NULL);
        return NULL;
    }
    return self;
}

int audioio_player_play(audioio_player * self) {
    if (self == NULL) return 0;
    if (atomic_load(&self->position) >= self->frames) atomic_store(&self->position, 0);
    atomic_store(&self->playing, 1);
    return 1;
}

int audioio_player_pause(audioio_player * self) {
    if (self == NULL) return 0;
    atomic_store(&self->playing, 0);
    return 1;
}

void audioio_player_close(audioio_player * self) {
    if (self == NULL) return;
    atomic_store(&self->playing, 0);
    ma_device_uninit(&self->device);   /* joins the audio thread before the free */
    ma_free(self->samples, NULL);
    ma_free(self, NULL);
}

size_t audioio_player_position(const audioio_player * self) {
    if (self == NULL) return 0;
    return atomic_load(&((audioio_player *)self)->position);
}

void audioio_player_seek(audioio_player * self, size_t frame) {
    if (self == NULL) return;
    atomic_store(&self->position, frame > self->frames ? self->frames : frame);
}

size_t audioio_player_length(const audioio_player * self) {
    return self == NULL ? 0 : self->frames;
}

int audioio_player_is_playing(const audioio_player * self) {
    if (self == NULL) return 0;
    return atomic_load(&((audioio_player *)self)->playing);
}
