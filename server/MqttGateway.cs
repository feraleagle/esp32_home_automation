using System.Buffers.Binary;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace HomeAutomation;

public sealed class MqttSettings
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 1883;
    public bool UseTls { get; set; } = false;
    public string? CaCertificatePath { get; set; }
    public string ClientId { get; set; } = "home-automation-server";
    public string? Username { get; set; }
    public string? Password { get; set; }
}

// The project uses only MQTT 3.1.1 CONNECT, SUBSCRIBE, PUBLISH, PUBACK, and PING.
// This small client avoids an unavailable external package and keeps broker setup
// independent from the web app. The broker remains the Arch Linux MQTT service.
public sealed class MqttGateway(
    IConfiguration configuration,
    IHostEnvironment environment,
    IServiceScopeFactory scopes,
    ILogger<MqttGateway> logger) : BackgroundService
{
    private readonly MqttSettings settings = configuration.GetSection("Mqtt").Get<MqttSettings>() ?? new();
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private Stream? stream;
    private int nextPacketId;
    public bool IsConnected { get; private set; }
    public string? LastError { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunConnectionAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                LastError = ex.Message;
                logger.LogWarning(ex, "MQTT connection to {Host}:{Port} unavailable; retrying",
                    settings.Host, settings.Port);
            }
            finally
            {
                IsConnected = false;
                stream = null;
            }

            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task RunConnectionAsync(CancellationToken token)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(settings.Host, settings.Port, token);
        Stream connection = tcp.GetStream();
        if (settings.UseTls)
        {
            var tls = new SslStream(connection, leaveInnerStreamOpen: false);
            var options = new SslClientAuthenticationOptions
            {
                TargetHost = settings.Host,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
            };
            if (!string.IsNullOrWhiteSpace(settings.CaCertificatePath))
            {
                var caPath = Path.IsPathRooted(settings.CaCertificatePath) ? settings.CaCertificatePath :
                    Path.GetFullPath(Path.Combine(environment.ContentRootPath, settings.CaCertificatePath));
                if (!File.Exists(caPath)) throw new FileNotFoundException("MQTT CA certificate not found", caPath);
                var ca = X509Certificate2.CreateFromPemFile(caPath);
                options.CertificateChainPolicy = new X509ChainPolicy
                {
                    TrustMode = X509ChainTrustMode.CustomRootTrust,
                    RevocationMode = X509RevocationMode.NoCheck
                };
                options.CertificateChainPolicy.CustomTrustStore.Add(ca);
            }
            await tls.AuthenticateAsClientAsync(options, token);
            connection = tls;
        }

        using (connection)
        {
            await WritePacketAsync(connection, 0x10, BuildConnectPayload(), token);
            var connack = await ReadPacketAsync(connection, token);
            if (connack.Type != 2 || connack.Data.Length != 2 || connack.Data[1] != 0)
                throw new IOException($"Broker rejected MQTT CONNECT (code {connack.Data.LastOrDefault()}).");

            stream = connection;
            var subscribeId = await SubscribeAsync(token);
            using (var subscribeTimeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                subscribeTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                while (true)
                {
                    var reply = await ReadPacketAsync(connection, subscribeTimeout.Token);
                    if (reply.Type == 3)
                    {
                        await HandlePublishAsync(reply, token);
                        continue;
                    }
                    if (reply.Type != 9 || reply.Data.Length < 5 ||
                        BinaryPrimitives.ReadUInt16BigEndian(reply.Data) != subscribeId ||
                        reply.Data[2] > 1 || reply.Data[3] > 1 || reply.Data[4] > 1)
                        throw new IOException("Broker did not accept MQTT subscriptions.");
                    break;
                }
            }
            IsConnected = true;
            LastError = null;
            logger.LogInformation("MQTT connected to {Host}:{Port}", settings.Host, settings.Port);

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
            var pingTask = PingLoopAsync(linked.Token);
            try
            {
                while (!token.IsCancellationRequested)
                {
                    using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    readTimeout.CancelAfter(TimeSpan.FromSeconds(75));
                    var packet = await ReadPacketAsync(connection, readTimeout.Token);
                    if (packet.Type == 3) await HandlePublishAsync(packet, token);
                }
            }
            finally
            {
                linked.Cancel();
                IsConnected = false;
                try { await pingTask; } catch (OperationCanceledException) { }
            }
        }
    }

    private byte[] BuildConnectPayload()
    {
        using var buffer = new MemoryStream();
        WriteString(buffer, "MQTT");
        buffer.WriteByte(4); // MQTT 3.1.1
        byte flags = 0x02; // clean session
        if (settings.Username is not null) flags |= 0x80;
        if (settings.Password is not null) flags |= 0x40;
        buffer.WriteByte(flags);
        buffer.Write([0, 30]); // keepalive seconds
        WriteString(buffer, settings.ClientId);
        if (settings.Username is not null) WriteString(buffer, settings.Username);
        if (settings.Password is not null) WriteString(buffer, settings.Password);
        return buffer.ToArray();
    }

    private async Task<ushort> SubscribeAsync(CancellationToken token)
    {
        using var buffer = new MemoryStream();
        var id = NextPacketId();
        WriteUInt16(buffer, id);
        WriteString(buffer, "devices/+/status");
        buffer.WriteByte(1);
        WriteString(buffer, "devices/+/led/state");
        buffer.WriteByte(1);
        WriteString(buffer, "devices/+/sensor/state");
        buffer.WriteByte(1);
        await WriteAsync(0x82, buffer.ToArray(), token);
        return id;
    }

    public async Task PublishCommandAsync(string deviceId, int index, bool state, CancellationToken token)
        => await PublishOutputAsync(deviceId, index, new { led = index, state }, token);

    public async Task PublishLevelAsync(string deviceId, int index, int level, CancellationToken token)
        => await PublishOutputAsync(deviceId, index, new { led = index, level }, token);

    public async Task PublishModeAsync(string deviceId, int index, string mode, CancellationToken token)
        => await PublishOutputAsync(deviceId, index, new { led = index, mode }, token);

    private async Task PublishOutputAsync<T>(string deviceId, int index, T command, CancellationToken token)
    {
        if (!IsConnected || stream is null) throw new IOException("MQTT broker is disconnected.");
        using var buffer = new MemoryStream();
        WriteString(buffer, $"devices/{deviceId}/led/cmd");
        WriteUInt16(buffer, NextPacketId());
        buffer.Write(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(command)));
        await WriteAsync(0x32, buffer.ToArray(), token); // QoS 1, no retain
    }

    private async Task HandlePublishAsync(MqttPacket packet, CancellationToken token)
    {
        var span = packet.Data.AsSpan();
        if (span.Length < 2) return;
        var topicLength = BinaryPrimitives.ReadUInt16BigEndian(span);
        if (span.Length < 2 + topicLength) return;
        var topic = Encoding.UTF8.GetString(span.Slice(2, topicLength));
        var offset = 2 + topicLength;
        var qos = (packet.Header >> 1) & 3;
        ushort packetId = 0;
        if (qos > 0)
        {
            if (span.Length < offset + 2) return;
            packetId = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(offset, 2));
            offset += 2;
        }
        if (qos == 1)
        {
            using var ack = new MemoryStream();
            WriteUInt16(ack, packetId);
            await WriteAsync(0x40, ack.ToArray(), token);
        }
        if (qos > 1) return;

        var parts = topic.Split('/');
        if (parts.Length < 3 || parts[0] != "devices" || !ValidDeviceId(parts[1])) return;
        var payload = packet.Data.AsMemory(offset);
        try
        {
            if (parts.Length == 3 && parts[2] == "status")
            {
                var status = Encoding.UTF8.GetString(payload.Span);
                if (status == "online" || status == "offline")
                    await UpdateNodeAsync(parts[1], status == "online", null, token);
            }
            else if (parts.Length == 4 && parts[2] == "led" && parts[3] == "state")
            {
                using var json = JsonDocument.Parse(payload);
                if (json.RootElement.ValueKind == JsonValueKind.Object &&
                    json.RootElement.TryGetProperty("leds", out var leds) && leds.ValueKind == JsonValueKind.Array)
                    await UpdateNodeAsync(parts[1], true, leds, token);
            }
            else if (parts.Length == 4 && parts[2] == "sensor" && parts[3] == "state")
            {
                using var json = JsonDocument.Parse(payload);
                if (json.RootElement.ValueKind == JsonValueKind.Object &&
                    json.RootElement.TryGetProperty("sensors", out var sensors) && sensors.ValueKind == JsonValueKind.Array)
                    await UpdateSensorsAsync(parts[1], sensors, token);
            }
        }
        catch (Exception ex) when (ex is JsonException or DbUpdateException)
        {
            logger.LogWarning(ex, "Ignored invalid MQTT message on {Topic}", topic);
        }
    }

    private async Task UpdateNodeAsync(string deviceId, bool online, JsonElement? leds, CancellationToken token)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HomeDb>();
        var node = await db.Nodes.Include(x => x.Outputs).FirstOrDefaultAsync(x => x.DeviceId == deviceId, token);
        if (node is null)
        {
            node = new Node { DeviceId = deviceId, Name = $"ESP32 {(deviceId.Length > 6 ? deviceId[^6..] : deviceId)}" };
            db.Nodes.Add(node);
        }
        node.IsOnline = online;
        node.LastSeenUtc = DateTime.UtcNow;
        if (leds is { } array)
        {
            node.Kind = "actuator";
            foreach (var led in array.EnumerateArray())
            {
                if (led.ValueKind != JsonValueKind.Object) continue;
                if (!led.TryGetProperty("id", out var id) || !id.TryGetInt32(out var index) || index is < 0 or > 31 ||
                    !led.TryGetProperty("gpio", out var pin) || !pin.TryGetInt32(out var gpio) ||
                    !led.TryGetProperty("state", out var state) || state.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) continue;
                var output = node.Outputs.FirstOrDefault(x => x.Index == index);
                if (output is null)
                {
                    output = new Output { Index = index, Node = node };
                    node.Outputs.Add(output);
                }
                output.Gpio = gpio;
                output.IsOn = state.GetBoolean();
                output.Mode = led.TryGetProperty("mode", out var mode) && mode.ValueKind == JsonValueKind.String &&
                    mode.GetString() == "pwm" ? "pwm" : "digital";
                output.Level = output.Mode == "pwm" && led.TryGetProperty("level", out var level) &&
                    level.ValueKind == JsonValueKind.Number && level.TryGetInt32(out var percentage) &&
                    percentage is >= 0 and <= 100
                    ? percentage : output.IsOn ? 100 : 0;
            }
        }
        await db.SaveChangesAsync(token);
    }

    private async Task UpdateSensorsAsync(string deviceId, JsonElement readings, CancellationToken token)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HomeDb>();
        var node = await db.Nodes.Include(x => x.Sensors).FirstOrDefaultAsync(x => x.DeviceId == deviceId, token);
        if (node is null)
        {
            node = new Node { DeviceId = deviceId, Name = $"Sensor {(deviceId.Length > 6 ? deviceId[^6..] : deviceId)}" };
            db.Nodes.Add(node);
        }
        node.Kind = "sensor";
        node.IsOnline = true;
        node.LastSeenUtc = DateTime.UtcNow;
        var valid = new List<(string Key, double Value)>();
        foreach (var reading in readings.EnumerateArray().Take(32))
        {
            if (reading.ValueKind != JsonValueKind.Object) continue;
            if (!reading.TryGetProperty("key", out var keyValue) || keyValue.ValueKind != JsonValueKind.String ||
                !reading.TryGetProperty("value", out var valueValue)) continue;
            double value;
            if (valueValue.ValueKind == JsonValueKind.True) value = 1;
            else if (valueValue.ValueKind == JsonValueKind.False) value = 0;
            else if (valueValue.ValueKind != JsonValueKind.Number || !valueValue.TryGetDouble(out value) || !double.IsFinite(value)) continue;
            var key = keyValue.GetString() ?? "";
            if (key.Length is < 1 or > 40 || !key.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')) continue;
            var unit = reading.TryGetProperty("unit", out var unitValue) && unitValue.ValueKind == JsonValueKind.String
                ? unitValue.GetString() ?? "" : "";
            if (unit.Length > 20) unit = unit[..20];
            var channel = node.Sensors.FirstOrDefault(x => x.Key == key);
            if (channel is null)
            {
                channel = new SensorChannel { Key = key, Node = node };
                node.Sensors.Add(channel);
            }
            channel.Value = value;
            channel.Unit = unit;
            channel.UpdatedAtUtc = DateTime.UtcNow;
            valid.Add((key, value));
        }
        await db.SaveChangesAsync(token);
        var automation = scope.ServiceProvider.GetRequiredService<AutomationService>();
        foreach (var (key, value) in valid)
            await automation.ProcessSensorAsync(node.Id, key, value, token);
    }

    private async Task PingLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(15), token);
            await WriteAsync(0xC0, [], token);
        }
    }

    private async Task WriteAsync(byte header, byte[] payload, CancellationToken token)
    {
        var current = stream ?? throw new IOException("MQTT broker is disconnected.");
        await writeLock.WaitAsync(token);
        try { await WritePacketAsync(current, header, payload, token); }
        finally { writeLock.Release(); }
    }

    private static async Task WritePacketAsync(Stream target, byte header, byte[] payload, CancellationToken token)
    {
        using var packet = new MemoryStream();
        packet.WriteByte(header);
        var length = payload.Length;
        do
        {
            var digit = length % 128;
            length /= 128;
            if (length > 0) digit |= 0x80;
            packet.WriteByte((byte)digit);
        } while (length > 0);
        packet.Write(payload);
        await target.WriteAsync(packet.ToArray(), token);
    }

    private static async Task<MqttPacket> ReadPacketAsync(Stream source, CancellationToken token)
    {
        var one = new byte[1];
        await source.ReadExactlyAsync(one, token);
        var header = one[0];
        var length = 0;
        var multiplier = 1;
        for (var i = 0; i < 4; i++)
        {
            await source.ReadExactlyAsync(one, token);
            length += (one[0] & 0x7F) * multiplier;
            if ((one[0] & 0x80) == 0) break;
            if (i == 3) throw new IOException("Invalid MQTT packet length.");
            multiplier *= 128;
        }
        if (length > 65536) throw new IOException("MQTT packet exceeds size limit.");
        var data = new byte[length];
        await source.ReadExactlyAsync(data, token);
        return new MqttPacket(header, data);
    }

    private static void WriteString(Stream target, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(value));
        WriteUInt16(target, (ushort)bytes.Length);
        target.Write(bytes);
    }

    private static void WriteUInt16(Stream target, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        target.Write(bytes);
    }

    private ushort NextPacketId() => (ushort)((Interlocked.Increment(ref nextPacketId) - 1) % ushort.MaxValue + 1);
    public static bool ValidDeviceId(string value) => value.Length is > 0 and <= 64 &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private sealed record MqttPacket(byte Header, byte[] Data)
    {
        public int Type => Header >> 4;
    }
}
