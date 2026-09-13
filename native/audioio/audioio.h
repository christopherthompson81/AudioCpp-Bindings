/*
 * audioio - a small C ABI over miniaudio for recording and playback.
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
#ifndef AUDIOIO_H
#define AUDIOIO_H

#include <stddef.h>

#if defined(_WIN32)
  #if defined(AUDIOIO_BUILD)
    #define AUDIOIO_API __declspec(dllexport)
  #else
    #define AUDIOIO_API __declspec(dllimport)
  #endif
#else
  #define AUDIOIO_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct audioio_device audioio_device;

/* Packed major.minor.revision, matching audiocpp_abi_version's shape. */
AUDIOIO_API unsigned int audioio_version(void);

/* Backend actually selected at runtime, e.g. "PulseAudio". Never NULL. */
AUDIOIO_API const char * audioio_backend(void);

/*
 * Refresh and count the capture devices. Call before indexing names; the
 * indices are only meaningful until the next refresh.
 */
AUDIOIO_API int audioio_refresh_devices(void);
AUDIOIO_API int audioio_device_count(void);
AUDIOIO_API const char * audioio_device_name(int index);

/* True for the device the system would pick on its own. */
AUDIOIO_API int audioio_device_is_default(int index);

/*
 * Open a capture device. Pass -1 for the system default. The device converts
 * to the requested rate and channel count itself, so a caller wanting 16 kHz
 * mono for ASR gets it regardless of what the hardware runs at.
 *
 * Returns NULL on failure and fills err when err_len > 0.
 */
AUDIOIO_API audioio_device * audioio_open(
    int device_index, int sample_rate, int channels, char * err, size_t err_len);

AUDIOIO_API int audioio_start(audioio_device * device);
AUDIOIO_API int audioio_stop(audioio_device * device);
AUDIOIO_API void audioio_close(audioio_device * device);

/*
 * Drain up to frame_count frames into out. Returns the number of frames
 * actually written, which may be zero. Never blocks.
 */
AUDIOIO_API size_t audioio_read(
    audioio_device * device, float * out, size_t frame_count);

/*
 * Frames dropped because the caller did not drain fast enough, since the last
 * call to this function. A UI showing anything other than zero here is telling
 * the user their recording has holes in it.
 */
AUDIOIO_API unsigned long long audioio_overruns(audioio_device * device);

/* Peak absolute sample since the last call, 0..1, for a level meter. */
AUDIOIO_API float audioio_peak(audioio_device * device);

/* ------------------------------------------------------------------ */
/* Playback                                                            */
/* ------------------------------------------------------------------ */
/*
 * A preview player: hand it a clip, then play, pause and seek within it.
 *
 * The clip is copied on open rather than borrowed, because the managed caller
 * owns an array the garbage collector may move, and the audio thread reads it
 * without any knowledge of that. Copying costs one allocation per preview and
 * removes a whole class of very hard bug.
 */
typedef struct audioio_player audioio_player;

AUDIOIO_API audioio_player * audioio_player_open(
    const float * samples, size_t frame_count,
    int sample_rate, int channels, char * err, size_t err_len);

AUDIOIO_API int  audioio_player_play(audioio_player * player);
AUDIOIO_API int  audioio_player_pause(audioio_player * player);
AUDIOIO_API void audioio_player_close(audioio_player * player);

/* Playhead in frames. Seeking past the end stops at the end. */
AUDIOIO_API size_t audioio_player_position(const audioio_player * player);
AUDIOIO_API void   audioio_player_seek(audioio_player * player, size_t frame);
AUDIOIO_API size_t audioio_player_length(const audioio_player * player);

/* Non-zero while sound is actually being produced. */
AUDIOIO_API int audioio_player_is_playing(const audioio_player * player);

#ifdef __cplusplus
}
#endif

#endif /* AUDIOIO_H */
