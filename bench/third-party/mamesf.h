/* Minimal SoftFloat host-environment shim (replaces MAME's mamesf.h) — integer types + flag only.
 * Musashi's softfloat/milieu.h #includes this for IEC/IEEE softfloat; our 68000-integer benchmark
 * never executes FP ops, so this just satisfies the type contract so the core compiles + links. */
#ifndef MAMESF_H
#define MAMESF_H
#include <stdint.h>
typedef uint8_t  flag;
typedef uint8_t  uint8;
typedef int8_t   int8;
typedef uint16_t uint16;
typedef int16_t  int16;
typedef uint32_t uint32;
typedef int32_t  int32;
typedef uint64_t uint64;
typedef int64_t  int64;
typedef uint8_t  bits8;
typedef int8_t   sbits8;
typedef uint16_t bits16;
typedef int16_t  sbits16;
typedef uint32_t bits32;
typedef int32_t  sbits32;
typedef uint64_t bits64;
typedef int64_t  sbits64;
#define LIT64(x) x##ULL
#define INLINE static inline
#endif
