# Stands in for esp32-arduino-lib-builder's arduino_tinyusb component when env:deck rebuilds
# ESP-IDF: the settings (Kconfig.projbuild), and Arduino's own headers and prebuilt library
# from the framework-arduinoespressif32-libs package, used as they are. The rebuild doesn't
# replace that library (nothing here compiles it), so it's always the stock one.
if(CONFIG_TINYUSB_ENABLED)
  idf_build_get_property(idf_path IDF_PATH)
  idf_build_get_property(target IDF_TARGET)
  get_filename_component(libs "${idf_path}/../framework-arduinoespressif32-libs/${target}" ABSOLUTE)
  set(lib "${libs}/lib/libarduino_tinyusb.a")
  set(inc "${libs}/include/arduino_tinyusb")
  if(NOT EXISTS "${lib}" OR NOT EXISTS "${inc}/include/tusb_config.h")
    message(FATAL_ERROR "arduino_tinyusb: Arduino's TinyUSB library not found under ${libs}")
  endif()
  idf_component_register(INCLUDE_DIRS "${inc}/include" "${inc}/tinyusb/src"
                         REQUIRES freertos soc esp_rom esp_hw_support usb log)
  add_prebuilt_library(arduino_tinyusb_stock "${lib}"
                       REQUIRES freertos soc esp_rom esp_hw_support usb log)
  target_link_libraries(${COMPONENT_LIB} INTERFACE arduino_tinyusb_stock)
else()
  idf_component_register()
endif()
