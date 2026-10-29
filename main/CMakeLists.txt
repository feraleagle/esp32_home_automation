if(EXISTS "${CMAKE_CURRENT_LIST_DIR}/mqtt_ca_cert.pem")
    set(MQTT_CA_CERT_FILE "mqtt_ca_cert.pem")
endif()

idf_component_register(SRCS "esp32_init.c"
                    INCLUDE_DIRS "."
                    PRIV_REQUIRES
                        esp_driver_gpio
                        esp_driver_ledc
                        esp_event
                        esp_hw_support
                        esp_netif
                        esp_wifi
                        freertos
                        log
                        mbedtls
                        nvs_flash
                        espressif__cjson
                        espressif__mdns
                        espressif__mqtt
                    EMBED_TXTFILES ${MQTT_CA_CERT_FILE})

if(MQTT_CA_CERT_FILE)
    target_compile_definitions(${COMPONENT_LIB} PRIVATE MQTT_CUSTOM_CA=1)
endif()
