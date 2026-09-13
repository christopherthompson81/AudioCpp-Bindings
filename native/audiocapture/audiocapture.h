/*
 * audiocapture - a small C ABI over miniaudio for recording and playback.
 *
 * Pull-based on purpose. miniaudio drives a callback on its own thread; that
 * callback stays on this side of the boundary and writes into a ring buffer,
 * and the managed caller drains it. No function pointer crosses the FFI, which
 * is the same rule audiocpp's own streaming API follows, and it keeps the
 * managed side free of the "do not allocate, do not block, do not take a lock"
 * constraints a real-time audio callback imposes.
 *
 * Every entry point is safe to call with NULL. Strings are owned by this
 * library, never freed by the caller, and stay valid until the next call that
 * changes the device list.
 */
#ifndef AUDIOCAPTURE_H
#define AUDIOCAPTURE_H

#include <stddef.h>

#if defined(_WIN32)
  #if defined(AUDIOCAPTURE_BUILD)
    #define AUDIOCAPTURE_API __declspec(dllexport)
  #else
    #define AUDIOCAPTURE_API __declspec(dllimport)
  #endif
#else
  #define AUDIOCAPTURE_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct audiocapture_device audiocapture_device;

/* Packed major.minor.revision, matching audiocpp_abi_version's shape. */
AUDIOCAPTURE_API unsigned int audiocapture_version(void);

/* Backend actually selected at runtime, e.g. "PulseAudio". Never NULL. */
AUDIOCAPTURE_API const char * audiocapture_backend(void);

/*
 * Refresh and count the capture devices. Call before indexing names; the
 * indices are only meaningful until the next refresh.
 */
AUDIOCAPTURE_API int audiocapture_refresh_devices(void);
AUDIOCAPTURE_API int audiocapture_device_count(void);
AUDIOCAPTURE_API const char * audiocapture_device_name(int index);

/* True for the device the system would pick on its own. */
AUDIOCAPTURE_API int audiocapture_device_is_default(int index);

/*
 * Open a capture device. Pass -1 for the system default. The device converts
 * to the requested rate and channel count itself, so a caller wanting 16 kHz
 * mono for ASR gets it regardless of what the hardware runs at.
 *
 * Returns NULL on failure and fills err when err_len > 0.
 */
AUDIOCAPTURE_API audiocapture_device * audiocapture_open(
    int device_index, int sample_rate, int channels, char * err, size_t err_len);

AUDIOCAPTURE_API int audiocapture_start(audiocapture_device * device);
AUDIOCAPTURE_API int audiocapture_stop(audiocapture_device * device);
AUDIOCAPTURE_API void audiocapture_close(audiocapture_device * device);

/*
 * Drain up to frame_count frames into out. Returns the number of frames
 * actually written, which may be zero. Never blocks.
 */
AUDIOCAPTURE_API size_t audiocapture_read(
    audiocapture_device * device, float * out, size_t frame_count);

/*
 * Frames dropped because the caller did not drain fast enough, since the last
 * call to this function. A UI showing anything other than zero here is telling
 * the user their recording has holes in it.
 */
AUDIOCAPTURE_API unsigned long long audiocapture_overruns(audiocapture_device * device);

/* Peak absolute sample since the last call, 0..1, for a level meter. */
AUDIOCAPTURE_API float audiocapture_peak(audiocapture_device * device);

#ifdef __cplusplus
}
#endif

#endif /* AUDIOCAPTURE_H */
