/*
 * protocol_check.c
 *
 * パケットのレイアウトが仕様どおりかを確かめる。
 * C 側と C# 側は「同じバイト列」でしか合意していないので、
 * どちらかのメンバを足し引きしたときにここで落ちるようにしておく。
 *
 * C# 側 (Assets/Scripts/Net/EtsimPacket.cs) のオフセットとこの表を
 * 突き合わせること。両方を直さない限りテストは通らない。
 */
#include <stddef.h>
#include <stdio.h>

#include "etsim_protocol.h"

static int failures;

static void check_size(const char *what, size_t actual, size_t expected)
{
    if (actual != expected) {
        printf("NG  %-40s サイズ %zu (期待 %zu)\n", what, actual, expected);
        failures++;
    } else {
        printf("ok  %-40s サイズ %zu\n", what, actual);
    }
}

static void check_offset(const char *what, size_t actual, size_t expected)
{
    if (actual != expected) {
        printf("NG  %-40s オフセット %zu (期待 %zu)\n", what, actual, expected);
        failures++;
    }
}

int main(void)
{
    check_size("etsim_sensor_packet_t", sizeof(etsim_sensor_packet_t), 152);
    check_size("etsim_actuator_packet_t", sizeof(etsim_actuator_packet_t), 44);

    check_offset("sensor.magic",        offsetof(etsim_sensor_packet_t, magic), 0);
    check_offset("sensor.version",      offsetof(etsim_sensor_packet_t, version), 4);
    check_offset("sensor.flags",        offsetof(etsim_sensor_packet_t, flags), 6);
    check_offset("sensor.seq",          offsetof(etsim_sensor_packet_t, seq), 8);
    check_offset("sensor.ack_seq",      offsetof(etsim_sensor_packet_t, ack_seq), 12);
    check_offset("sensor.sim_time_us",  offsetof(etsim_sensor_packet_t, sim_time_us), 16);
    check_offset("sensor.color_id",     offsetof(etsim_sensor_packet_t, color_id), 20);
    check_offset("sensor.reflection",   offsetof(etsim_sensor_packet_t, reflection), 24);
    check_offset("sensor.ambient",      offsetof(etsim_sensor_packet_t, ambient), 28);
    check_offset("sensor.rgb",          offsetof(etsim_sensor_packet_t, rgb), 32);
    check_offset("sensor.hsv_h",        offsetof(etsim_sensor_packet_t, hsv_h), 38);
    check_offset("sensor.hsv_s",        offsetof(etsim_sensor_packet_t, hsv_s), 40);
    check_offset("sensor.hsv_v",        offsetof(etsim_sensor_packet_t, hsv_v), 41);
    check_offset("sensor.ultrasonic_mm", offsetof(etsim_sensor_packet_t, ultrasonic_mm), 44);
    check_offset("sensor.ultrasonic_presence", offsetof(etsim_sensor_packet_t, ultrasonic_presence), 48);
    check_offset("sensor.force_pressed", offsetof(etsim_sensor_packet_t, force_pressed), 49);
    check_offset("sensor.force_newton", offsetof(etsim_sensor_packet_t, force_newton), 52);
    check_offset("sensor.yaw_deg",      offsetof(etsim_sensor_packet_t, yaw_deg), 56);
    check_offset("sensor.pitch_deg",    offsetof(etsim_sensor_packet_t, pitch_deg), 60);
    check_offset("sensor.roll_deg",     offsetof(etsim_sensor_packet_t, roll_deg), 64);
    check_offset("sensor.gyro_dps",     offsetof(etsim_sensor_packet_t, gyro_dps), 68);
    check_offset("sensor.accel_mss",    offsetof(etsim_sensor_packet_t, accel_mss), 80);
    check_offset("sensor.motor_count",  offsetof(etsim_sensor_packet_t, motor_count), 92);
    check_offset("sensor.motor_speed",  offsetof(etsim_sensor_packet_t, motor_speed), 104);
    check_offset("sensor.motor_power",  offsetof(etsim_sensor_packet_t, motor_power), 116);
    check_offset("sensor.button_state", offsetof(etsim_sensor_packet_t, button_state), 128);
    check_offset("sensor.battery_mv",   offsetof(etsim_sensor_packet_t, battery_mv), 132);
    check_offset("sensor.truth_x_mm",   offsetof(etsim_sensor_packet_t, truth_x_mm), 136);
    check_offset("sensor.truth_y_mm",   offsetof(etsim_sensor_packet_t, truth_y_mm), 140);
    check_offset("sensor.truth_heading_deg", offsetof(etsim_sensor_packet_t, truth_heading_deg), 144);
    check_offset("sensor.truth_speed_mmps", offsetof(etsim_sensor_packet_t, truth_speed_mmps), 148);

    check_offset("actuator.magic",        offsetof(etsim_actuator_packet_t, magic), 0);
    check_offset("actuator.version",      offsetof(etsim_actuator_packet_t, version), 4);
    check_offset("actuator.flags",        offsetof(etsim_actuator_packet_t, flags), 6);
    check_offset("actuator.seq",          offsetof(etsim_actuator_packet_t, seq), 8);
    check_offset("actuator.ctrl_time_us", offsetof(etsim_actuator_packet_t, ctrl_time_us), 12);
    check_offset("actuator.motor_power",  offsetof(etsim_actuator_packet_t, motor_power), 16);
    check_offset("actuator.motor_mode",   offsetof(etsim_actuator_packet_t, motor_mode), 28);
    check_offset("actuator.reset_count_mask", offsetof(etsim_actuator_packet_t, reset_count_mask), 31);
    check_offset("actuator.led_rgb",      offsetof(etsim_actuator_packet_t, led_rgb), 32);
    check_offset("actuator.reset_imu",    offsetof(etsim_actuator_packet_t, reset_imu), 35);
    check_offset("actuator.display",      offsetof(etsim_actuator_packet_t, display), 36);

    /* リトルエンディアン前提のプロトコルなので、実行環境も確認する */
    {
        const uint32_t probe = 0x01020304u;
        const unsigned char *bytes = (const unsigned char *)&probe;
        if (bytes[0] != 0x04) {
            printf("NG  このプロトコルはリトルエンディアン専用です\n");
            failures++;
        }
    }

    if (failures == 0) {
        printf("\nすべて一致しました\n");
        return 0;
    }
    printf("\n%d 件の不一致があります\n", failures);
    return 1;
}
