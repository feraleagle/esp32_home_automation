#include <math.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

#include "cJSON.h"
#include "driver/gpio.h"
#include "driver/ledc.h"
#include "esp_crt_bundle.h"
#include "esp_event.h"
#include "esp_log.h"
#include "esp_mac.h"
#include "esp_netif.h"
#include "esp_netif_sntp.h"
#include "esp_wifi.h"
#include "freertos/FreeRTOS.h"
#include "mdns.h"
#include "mqtt_client.h"
#include "nvs.h"
#include "nvs_flash.h"

/* Local test broker. Switch to mqtts:// and add a CA PEM for TLS later. */
#include "local_config.h"
#define NTP_SERVER "pool.ntp.org"

#ifdef MQTT_CUSTOM_CA
extern const char mqtt_ca_cert_pem_start[] asm("_binary_mqtt_ca_cert_pem_start");
#endif

static const char* TAG = "IOT";

typedef struct {
    gpio_num_t gpio;
    bool state;
    bool pwm;
    uint8_t level;
    uint8_t preset;
} led_t;

static led_t leds[] = {
    {GPIO_NUM_4, false, false, 0, 0},
    {GPIO_NUM_5, false, false, 0, 0},
    {GPIO_NUM_18, false, false, 0, 0},
    {GPIO_NUM_19, false, false, 0, 0},
    {GPIO_NUM_21, false, false, 0, 0},
};
#define LED_COUNT (sizeof(leds) / sizeof(leds[0]))

static char device_id[32];
static char topic_cmd[96];
static char topic_state[96];
static char topic_status[96];
static esp_mqtt_client_handle_t mqtt = NULL;
static bool mqtt_connected = false;
static bool mdns_started = false;
static bool sntp_started = false;
static bool time_synced = false;
static bool wifi_connected = false;
static nvs_handle_t output_settings = 0;

static bool mqtt_requires_clock(void) {
    return strncmp(MQTT_BROKER_URI, "mqtts://", 8) == 0;
}

#define PWM_FREQUENCY_HZ 5000
#define PWM_RESOLUTION LEDC_TIMER_10_BIT
#define PWM_MAX_DUTY 1023

static void save_output_settings(void) {
    if (!output_settings) return;
    uint8_t mask = 0;
    for (size_t i = 0; i < LED_COUNT; ++i) {
        if (leds[i].pwm) mask |= (uint8_t)(1U << i);
        char key[8];
        snprintf(key, sizeof(key), "level%u", (unsigned)i);
        ESP_ERROR_CHECK(nvs_set_u8(output_settings, key, leds[i].preset));
    }
    ESP_ERROR_CHECK(nvs_set_u8(output_settings, "pwm_mask", mask));
    ESP_ERROR_CHECK(nvs_commit(output_settings));
}

static esp_err_t configure_output(size_t index, bool pwm) {
    led_t* output = &leds[index];
    if (output->pwm) ESP_ERROR_CHECK(ledc_stop(LEDC_LOW_SPEED_MODE, (ledc_channel_t)index, 0));
    ESP_ERROR_CHECK(gpio_reset_pin(output->gpio));
    if (pwm) {
        ledc_channel_config_t channel = {
            .gpio_num = output->gpio,
            .speed_mode = LEDC_LOW_SPEED_MODE,
            .channel = (ledc_channel_t)index,
            .intr_type = LEDC_INTR_DISABLE,
            .timer_sel = LEDC_TIMER_0,
            .duty = 0,
            .hpoint = 0,
        };
        esp_err_t result = ledc_channel_config(&channel);
        if (result != ESP_OK) return result;
    } else {
        gpio_config_t cfg = {
            .pin_bit_mask = 1ULL << output->gpio,
            .mode = GPIO_MODE_OUTPUT,
            .pull_up_en = GPIO_PULLUP_DISABLE,
            .pull_down_en = GPIO_PULLDOWN_DISABLE,
            .intr_type = GPIO_INTR_DISABLE,
        };
        esp_err_t result = gpio_config(&cfg);
        if (result != ESP_OK) return result;
        result = gpio_set_level(output->gpio, 0);
        if (result != ESP_OK) return result;
    }
    output->pwm = pwm;
    output->state = false;
    output->level = 0;
    return ESP_OK;
}

static void leds_init(void) {
    ledc_timer_config_t timer = {
        .speed_mode = LEDC_LOW_SPEED_MODE,
        .duty_resolution = PWM_RESOLUTION,
        .timer_num = LEDC_TIMER_0,
        .freq_hz = PWM_FREQUENCY_HZ,
        .clk_cfg = LEDC_AUTO_CLK,
    };
    ESP_ERROR_CHECK(ledc_timer_config(&timer));
    uint8_t mask = 0;
    if (output_settings) nvs_get_u8(output_settings, "pwm_mask", &mask);
    for (size_t i = 0; i < LED_COUNT; ++i) {
        leds[i].preset = 100;
        if (output_settings) {
            char key[8];
            snprintf(key, sizeof(key), "level%u", (unsigned)i);
            nvs_get_u8(output_settings, key, &leds[i].preset);
        }
        if (leds[i].preset < 1 || leds[i].preset > 100) leds[i].preset = 100;
        ESP_ERROR_CHECK(configure_output(i, (mask & (1U << i)) != 0));
    }
}

static void publish_states(void) {
    if (!mqtt_connected) return;
    cJSON* root = cJSON_CreateObject();
    if (!root) return;
    cJSON* array = cJSON_AddArrayToObject(root, "leds");
    if (!array) {
        cJSON_Delete(root);
        return;
    }
    for (size_t i = 0; i < LED_COUNT; ++i) {
        cJSON* item = cJSON_CreateObject();
        if (!item) break;
        cJSON_AddNumberToObject(item, "id", (double)i);
        cJSON_AddNumberToObject(item, "gpio", (double)leds[i].gpio);
        cJSON_AddBoolToObject(item, "state", leds[i].state);
        cJSON_AddStringToObject(item, "mode", leds[i].pwm ? "pwm" : "digital");
        cJSON_AddNumberToObject(item, "level", leds[i].level);
        cJSON_AddItemToArray(array, item);
    }
    char* payload = cJSON_PrintUnformatted(root);
    if (payload) {
        esp_mqtt_client_enqueue(mqtt, topic_state, payload, 0, 1, 1, true);
        cJSON_free(payload);
    }
    cJSON_Delete(root);
}

static void set_level(size_t index, uint8_t level) {
    led_t* output = &leds[index];
    esp_err_t result;
    if (output->pwm) {
        uint32_t duty = (uint32_t)level * PWM_MAX_DUTY / 100;
        result = ledc_set_duty(LEDC_LOW_SPEED_MODE, (ledc_channel_t)index, duty);
        if (result == ESP_OK) result = ledc_update_duty(LEDC_LOW_SPEED_MODE, (ledc_channel_t)index);
    } else {
        result = gpio_set_level(output->gpio, level > 0);
    }
    if (result != ESP_OK) {
        ESP_LOGE(TAG, "Failed to set output %u: %s", (unsigned)index, esp_err_to_name(result));
        return;
    }
    output->level = output->pwm ? level : (level > 0 ? 100 : 0);
    output->state = level > 0;
    if (output->pwm && level > 0 && output->preset != level) {
        output->preset = level;
        save_output_settings();
    }
    ESP_LOGI(TAG, "Output %u GPIO %d = %u%%", (unsigned)index,
             (int)output->gpio, (unsigned)output->level);
}

static void set_led(size_t index, bool state) {
    set_level(index, state ? (leds[index].pwm ? leds[index].preset : 100) : 0);
}

/* Commands: {"led":0,"state":true}, {"led":2,"toggle":true},
 * {"led":0,"mode":"pwm"}, {"led":0,"level":45},
 * {"led":"all","state":false}
 */
static void handle_command(const char* payload) {
    cJSON* root = cJSON_Parse(payload);
    if (!root) {
        ESP_LOGW(TAG, "Invalid JSON");
        return;
    }
    const cJSON* led = cJSON_GetObjectItemCaseSensitive(root, "led");
    const cJSON* state = cJSON_GetObjectItemCaseSensitive(root, "state");
    const cJSON* toggle = cJSON_GetObjectItemCaseSensitive(root, "toggle");
    const cJSON* mode = cJSON_GetObjectItemCaseSensitive(root, "mode");
    const cJSON* level = cJSON_GetObjectItemCaseSensitive(root, "level");
    bool do_toggle = cJSON_IsTrue(toggle);
    if (cJSON_IsString(led) && strcmp(led->valuestring, "all") == 0) {
        if (!do_toggle && !cJSON_IsBool(state)) goto done;
        for (size_t i = 0; i < LED_COUNT; ++i)
            set_led(i, do_toggle ? !leds[i].state : cJSON_IsTrue(state));
    } else if (cJSON_IsNumber(led)) {
        double n = led->valuedouble;
        if (!isfinite(n) || n < 0 || n >= (double)LED_COUNT || n != floor(n)) {
            ESP_LOGW(TAG, "Invalid LED index");
            goto done;
        }
        size_t i = (size_t)n;
        if (mode) {
            if (!cJSON_IsString(mode) ||
                (strcmp(mode->valuestring, "pwm") != 0 && strcmp(mode->valuestring, "digital") != 0)) goto done;
            bool pwm = strcmp(mode->valuestring, "pwm") == 0;
            if (pwm != leds[i].pwm) {
                esp_err_t result = configure_output(i, pwm);
                if (result != ESP_OK) {
                    ESP_LOGE(TAG, "Mode change failed: %s", esp_err_to_name(result));
                    goto done;
                }
                save_output_settings();
            }
        } else if (level) {
            if (!leds[i].pwm || !cJSON_IsNumber(level) || !isfinite(level->valuedouble) ||
                level->valuedouble < 0 || level->valuedouble > 100 ||
                level->valuedouble != floor(level->valuedouble)) goto done;
            set_level(i, (uint8_t)level->valueint);
        } else if (do_toggle || cJSON_IsBool(state)) {
            set_led(i, do_toggle ? !leds[i].state : cJSON_IsTrue(state));
        } else goto done;
    } else {
        ESP_LOGW(TAG, "led must be a number or 'all'");
        goto done;
    }
    publish_states();
done:
    cJSON_Delete(root);
}

static void mqtt_event(void* arg, esp_event_base_t base, int32_t id, void* data) {
    esp_mqtt_event_handle_t e = data;
    switch ((esp_mqtt_event_id_t)id) {
        case MQTT_EVENT_CONNECTED:
            mqtt_connected = true;
            ESP_LOGI(TAG, "MQTT connected; subscribing to %s", topic_cmd);
            esp_mqtt_client_subscribe(mqtt, topic_cmd, 1);
            esp_mqtt_client_enqueue(mqtt, topic_status, "online", 0, 1, 1, true);
            publish_states();
            break;
        case MQTT_EVENT_DISCONNECTED:
            mqtt_connected = false;
            ESP_LOGW(TAG, "MQTT disconnected");
            break;
        case MQTT_EVENT_DATA:
            {
                if (e->topic_len != (int)strlen(topic_cmd) ||
                    memcmp(e->topic, topic_cmd, e->topic_len) != 0) break;
                /* This starter deliberately rejects fragmented/oversized commands. */
                if (e->current_data_offset != 0 || e->data_len != e->total_data_len ||
                    e->data_len <= 0 || e->data_len >= 256) {
                    ESP_LOGW(TAG, "Fragmented or oversized command rejected");
                    break;
                }
                char buf[256];
                memcpy(buf, e->data, e->data_len);
                buf[e->data_len] = '\0';
                handle_command(buf);
                break;
            }
        case MQTT_EVENT_ERROR:
            if (e->error_handle &&
                e->error_handle->error_type == MQTT_ERROR_TYPE_TCP_TRANSPORT) {
                ESP_LOGE(TAG, "MQTT TLS/transport error: esp=%s, tls=0x%x, verify=0x%x",
                         esp_err_to_name(e->error_handle->esp_tls_last_esp_err),
                         e->error_handle->esp_tls_stack_err,
                         e->error_handle->esp_tls_cert_verify_flags);
            } else {
                ESP_LOGW(TAG, "MQTT error");
            }
            break;
        default: break;
    }
}

static void mqtt_start_once(void) {
    if (mqtt) return;
    esp_mqtt_client_config_t cfg = {
        .broker.address.uri = MQTT_BROKER_URI,
        .credentials.client_id = device_id,
        .session.last_will.topic = topic_status,
        .session.last_will.msg = "offline",
        .session.last_will.qos = 1,
        .session.last_will.retain = true,
    };
    if (mqtt_requires_clock()) {
#ifdef MQTT_CUSTOM_CA
        cfg.broker.verification.certificate = mqtt_ca_cert_pem_start;
#else
        cfg.broker.verification.crt_bundle_attach = esp_crt_bundle_attach;
#endif
    }
    mqtt = esp_mqtt_client_init(&cfg);
    if (!mqtt) {
        ESP_LOGE(TAG, "MQTT init failed");
        return;
    }
    ESP_ERROR_CHECK(esp_mqtt_client_register_event(mqtt, ESP_EVENT_ANY_ID,
                                                   mqtt_event, NULL));
    ESP_ERROR_CHECK(esp_mqtt_client_start(mqtt));
}

static void wifi_event(
    void* arg,
    esp_event_base_t base,
    int32_t id,
    void* data) {
    if (base == WIFI_EVENT && id == WIFI_EVENT_STA_START) {
        esp_wifi_connect();
    } else if (base == WIFI_EVENT && id == WIFI_EVENT_STA_DISCONNECTED) {
        wifi_connected = false;
        wifi_event_sta_disconnected_t* e = data;
        ESP_LOGW(TAG, "Wi-Fi disconnected: reason=%d", e->reason);
        esp_wifi_connect();
    } else if (base == IP_EVENT && id == IP_EVENT_STA_GOT_IP) {
        wifi_connected = true;
        ip_event_got_ip_t* e = data;
        ESP_LOGI(TAG, "IP: " IPSTR, IP2STR(&e->ip_info.ip));
        /* Initialize mDNS once */
        if (!mdns_started) {
            ESP_ERROR_CHECK(mdns_init());
            mdns_started = true;
            ESP_LOGI(TAG, "mDNS initialized");
        }
        if (!sntp_started) {
            esp_sntp_config_t config = ESP_NETIF_SNTP_DEFAULT_CONFIG(NTP_SERVER);
            ESP_ERROR_CHECK(esp_netif_sntp_init(&config));
            sntp_started = true;
        }
        if (!mqtt_requires_clock() || time_synced) mqtt_start_once();
    } else if (base == NETIF_SNTP_EVENT && id == NETIF_SNTP_TIME_SYNC) {
        time_synced = true;
        ESP_LOGI(TAG, "Clock synchronized; starting MQTT");
        if (wifi_connected) mqtt_start_once();
    }
}

static void wifi_start(void) {
    ESP_ERROR_CHECK(esp_netif_init());
    ESP_ERROR_CHECK(esp_event_loop_create_default());
    esp_netif_create_default_wifi_sta();
    wifi_init_config_t init = WIFI_INIT_CONFIG_DEFAULT();
    ESP_ERROR_CHECK(esp_wifi_init(&init));
    ESP_ERROR_CHECK(esp_event_handler_register(WIFI_EVENT, ESP_EVENT_ANY_ID,
                                               wifi_event, NULL));
    ESP_ERROR_CHECK(esp_event_handler_register(IP_EVENT, IP_EVENT_STA_GOT_IP,
                                               wifi_event, NULL));
    ESP_ERROR_CHECK(esp_event_handler_register(NETIF_SNTP_EVENT, NETIF_SNTP_TIME_SYNC,
                                               wifi_event, NULL));
    wifi_config_t wifi = {.sta = {.ssid = WIFI_SSID,
                                  .password = WIFI_PASSWORD}};
    ESP_ERROR_CHECK(esp_wifi_set_mode(WIFI_MODE_STA));
    ESP_ERROR_CHECK(esp_wifi_set_config(WIFI_IF_STA, &wifi));
    ESP_ERROR_CHECK(esp_wifi_start());
}

void app_main(void) {
    esp_err_t err = nvs_flash_init();
    if (err == ESP_ERR_NVS_NO_FREE_PAGES || err == ESP_ERR_NVS_NEW_VERSION_FOUND) {
        ESP_ERROR_CHECK(nvs_flash_erase());
        err = nvs_flash_init();
    }
    ESP_ERROR_CHECK(err);

    err = nvs_open("outputs", NVS_READWRITE, &output_settings);
    if (err != ESP_OK) ESP_LOGW(TAG, "Output settings will not persist: %s", esp_err_to_name(err));

    uint8_t mac[6];
    ESP_ERROR_CHECK(esp_read_mac(mac, ESP_MAC_WIFI_STA));
    snprintf(device_id, sizeof(device_id),
             "esp32-%02x%02x%02x%02x%02x%02x",
             mac[0], mac[1], mac[2], mac[3], mac[4], mac[5]);
    snprintf(topic_cmd, sizeof(topic_cmd), "devices/%s/led/cmd", device_id);
    snprintf(topic_state, sizeof(topic_state), "devices/%s/led/state", device_id);
    snprintf(topic_status, sizeof(topic_status), "devices/%s/status", device_id);
    ESP_LOGI(TAG, "Device ID: %s", device_id);
    ESP_LOGI(TAG, "Command topic: %s", topic_cmd);

    leds_init();
    wifi_start();
}
