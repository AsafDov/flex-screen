// mbedTLS's base64, same contract: 0 on success, *olen set to the bytes written (or, when
// the buffer is too small, to the size needed).
#pragma once

#include <stddef.h>

#define MBEDTLS_ERR_BASE64_BUFFER_TOO_SMALL -0x002A
#define MBEDTLS_ERR_BASE64_INVALID_CHARACTER -0x002C

int mbedtls_base64_encode(unsigned char *dst, size_t dlen, size_t *olen, const unsigned char *src, size_t slen);
int mbedtls_base64_decode(unsigned char *dst, size_t dlen, size_t *olen, const unsigned char *src, size_t slen);
