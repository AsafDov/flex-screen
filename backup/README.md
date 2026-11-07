# Factory firmware backup

`factory-esp32s3-touch-lcd-7-E8F60AA7ECBC.bin` is a full 16 MB read of the board's flash,
taken on 2026-11-06 before anything else was flashed. It's Waveshare's factory demo
(bootloader at 0x0, partition table at 0x8000, app at 0x10000).

- Board: Waveshare ESP32-S3-Touch-LCD-7 (touch), MAC `e8:f6:0a:a7:ec:bc`
- Chip: ESP32-S3 QFN56 rev 0.2, 8 MB embedded PSRAM, 16 MB quad flash
- SHA-256: `3b1a2fd44978ec1472f36d0683ce6e0fbe0bd7944e5d484f5252cc6cc31e11c4`

## Restore

1. Plug into the board's **UART** port.
2. Hold **BOOT**, tap **RESET**, release **BOOT** (this port can't reset the chip itself).
3. Run, with the board's COM port:

```powershell
& "$env:USERPROFILE\.platformio\penv\Scripts\esptool.exe" --chip esp32s3 --port COM9 --baud 460800 --before no-reset write-flash 0 factory-esp32s3-touch-lcd-7-E8F60AA7ECBC.bin
```

4. Tap **RESET**.

921600 baud corrupts transfers on this port; stay at 460800.
