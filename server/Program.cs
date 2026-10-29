using HomeAutomation;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var databasePath = Path.Combine(builder.Environment.ContentRootPath, "home.db");
builder.Services.AddDbContextFactory<HomeDb>(options => options.UseSqlite($"Data Source={databasePath}"));
builder.Services.AddSingleton<MqttGateway>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<MqttGateway>());
builder.Services.AddSingleton<AutomationService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<AutomationService>());

var app = builder.Build();
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<HomeDb>();
    SchemaUpgrade.Apply(db);
}

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/dashboard", async (HomeDb db, MqttGateway mqtt, AutomationService automation) =>
{
    var areas = await db.Areas.AsNoTracking().OrderBy(x => x.Name)
        .Select(x => new { x.Id, x.Name, x.Color }).ToListAsync();
    var nodes = await db.Nodes.AsNoTracking().Include(x => x.Outputs).Include(x => x.Sensors)
        .OrderBy(x => x.Name).ToListAsync();
    var rules = await db.Rules.AsNoTracking().OrderBy(x => x.Name).ToListAsync();
    var schedules = await db.Schedules.AsNoTracking().OrderBy(x => x.Name).ToListAsync();
    var timers = await db.Timers.AsNoTracking().OrderByDescending(x => x.Id).Take(30).ToListAsync();
    return Results.Ok(new
    {
        areas,
        nodes = nodes.Select(x => new
        {
            x.Id, x.DeviceId, x.Name, x.Kind, x.AreaId, isOnline = mqtt.IsConnected && x.IsOnline, x.LastSeenUtc,
            outputs = x.Outputs.OrderBy(o => o.Index).Select(o => new { o.Index, o.Gpio, o.IsOn, o.Mode, o.Level }),
            sensors = x.Sensors.OrderBy(s => s.Key).Select(s => new { s.Key, s.Value, s.Unit, s.UpdatedAtUtc })
        }),
        rules = rules.Select(x => new { x.Id, x.Name, x.SensorNodeId, x.SensorKey, x.Operator, x.Threshold,
            x.ActuatorNodeId, x.OutputIndex, x.ActionState, x.ElseState, x.Enabled, x.LastTriggeredAtUtc }),
        schedules = schedules.Select(x => new { x.Id, x.Name, x.ActuatorNodeId, x.OutputIndex,
            x.StartMinute, x.EndMinute, x.DaysMask, x.Enabled }),
        timers = timers.Select(x => new { x.Id, x.Name, x.ActuatorNodeId, x.OutputIndex,
            x.DurationMinutes, x.ActionState,
            startedAtUtc = x.StartedAtUtc is null ? (DateTime?)null : DateTime.SpecifyKind(x.StartedAtUtc.Value, DateTimeKind.Utc),
            endsAtUtc = x.EndsAtUtc is null ? (DateTime?)null : DateTime.SpecifyKind(x.EndsAtUtc.Value, DateTimeKind.Utc),
            completedAtUtc = x.CompletedAtUtc is null ? (DateTime?)null : DateTime.SpecifyKind(x.CompletedAtUtc.Value, DateTimeKind.Utc) }),
        timeZone = automation.TimeZone.Id,
        broker = new { connected = mqtt.IsConnected, error = mqtt.LastError }
    });
});

app.MapPost("/api/areas", async (AreaInput input, HomeDb db) =>
{
    var name = input.Name?.Trim();
    if (string.IsNullOrEmpty(name) || name.Length > 60) return Results.BadRequest(new { error = "Area name must be 1–60 characters." });
    if (await db.Areas.AnyAsync(x => x.Name == name)) return Results.Conflict(new { error = "Area already exists." });
    var area = new Area { Name = name, Color = ValidColor(input.Color) ? input.Color! : "#4968e9" };
    db.Areas.Add(area);
    await db.SaveChangesAsync();
    return Results.Ok(new { area.Id });
});

app.MapPut("/api/areas/{id:int}", async (int id, AreaInput input, HomeDb db) =>
{
    var area = await db.Areas.FindAsync(id);
    if (area is null) return Results.NotFound();
    var name = input.Name?.Trim();
    if (string.IsNullOrEmpty(name) || name.Length > 60) return Results.BadRequest(new { error = "Area name must be 1–60 characters." });
    if (await db.Areas.AnyAsync(x => x.Id != id && x.Name == name)) return Results.Conflict(new { error = "Area already exists." });
    area.Name = name;
    if (ValidColor(input.Color)) area.Color = input.Color!;
    await db.SaveChangesAsync();
    return Results.NoContent();
});

app.MapDelete("/api/areas/{id:int}", async (int id, HomeDb db) =>
{
    var area = await db.Areas.FindAsync(id);
    if (area is null) return Results.NotFound();
    db.Areas.Remove(area);
    await db.SaveChangesAsync();
    return Results.NoContent();
});

app.MapPost("/api/nodes", async (NodeInput input, HomeDb db) =>
{
    var deviceId = input.DeviceId?.Trim() ?? "";
    if (!MqttGateway.ValidDeviceId(deviceId)) return Results.BadRequest(new { error = "Use a device ID of 1–64 letters, digits, hyphens, or underscores." });
    if (await db.Nodes.AnyAsync(x => x.DeviceId == deviceId)) return Results.Conflict(new { error = "Node already exists." });
    if (input.AreaId is int areaId && !await db.Areas.AnyAsync(x => x.Id == areaId)) return Results.BadRequest(new { error = "Area not found." });
    if (!ValidKind(input.Kind)) return Results.BadRequest(new { error = "Node type must be sensor or actuator." });
    var name = string.IsNullOrWhiteSpace(input.Name) ? deviceId : input.Name.Trim();
    if (name.Length > 80) return Results.BadRequest(new { error = "Node name is too long." });
    var node = new Node { DeviceId = deviceId, Name = name, AreaId = input.AreaId, Kind = input.Kind! };
    db.Nodes.Add(node);
    await db.SaveChangesAsync();
    return Results.Ok(new { node.Id });
});

app.MapPut("/api/nodes/{id:int}", async (int id, NodeEdit input, HomeDb db) =>
{
    var node = await db.Nodes.FindAsync(id);
    if (node is null) return Results.NotFound();
    var name = input.Name?.Trim();
    if (string.IsNullOrEmpty(name) || name.Length > 80) return Results.BadRequest(new { error = "Node name must be 1–80 characters." });
    if (input.AreaId is int areaId && !await db.Areas.AnyAsync(x => x.Id == areaId)) return Results.BadRequest(new { error = "Area not found." });
    if (!ValidKind(input.Kind)) return Results.BadRequest(new { error = "Node type must be sensor or actuator." });
    if (input.Kind != node.Kind &&
        (await db.Outputs.AnyAsync(x => x.NodeId == id) || await db.Sensors.AnyAsync(x => x.NodeId == id) ||
         await db.Rules.AnyAsync(x => x.SensorNodeId == id || x.ActuatorNodeId == id) ||
         await db.Schedules.AnyAsync(x => x.ActuatorNodeId == id) ||
         await db.Timers.AnyAsync(x => x.ActuatorNodeId == id && x.CompletedAtUtc == null)))
        return Results.BadRequest(new { error = "A node with reported channels or automations cannot change type." });
    node.Name = name;
    node.AreaId = input.AreaId;
    node.Kind = input.Kind!;
    await db.SaveChangesAsync();
    return Results.NoContent();
});

app.MapDelete("/api/nodes/{id:int}", async (int id, HomeDb db) =>
{
    var node = await db.Nodes.FindAsync(id);
    if (node is null) return Results.NotFound();
    if (await db.Timers.AnyAsync(x => x.ActuatorNodeId == id && x.CompletedAtUtc == null))
        return Results.Conflict(new { error = "Stop the active timer before removing this node." });
    db.Nodes.Remove(node);
    await db.SaveChangesAsync();
    return Results.NoContent();
});

app.MapPost("/api/nodes/{id:int}/outputs/{index:int}/command", async (
    int id, int index, OutputCommand command, HomeDb db, MqttGateway mqtt, CancellationToken token) =>
{
    var node = await db.Nodes.Include(x => x.Outputs).FirstOrDefaultAsync(x => x.Id == id, token);
    var output = node?.Outputs.FirstOrDefault(x => x.Index == index);
    if (node is null || output is null) return Results.NotFound();
    if ((command.State is null) == (command.Level is null))
        return Results.BadRequest(new { error = "Provide either state or level." });
    if (command.Level is { } level && (output.Mode != "pwm" || level is < 0 or > 100))
        return Results.BadRequest(new { error = "PWM level must be 0–100 on a PWM output." });
    if (await db.Rules.AnyAsync(x => x.Enabled && x.ActuatorNodeId == id && x.OutputIndex == index, token) ||
        await db.Schedules.AnyAsync(x => x.Enabled && x.ActuatorNodeId == id && x.OutputIndex == index, token) ||
        await db.Timers.AnyAsync(x => x.ActuatorNodeId == id && x.OutputIndex == index && x.CompletedAtUtc == null, token))
        return Results.Conflict(new { error = "This output is managed by an automation." });
    if (!mqtt.IsConnected) return Results.Json(new { error = "MQTT broker is disconnected." }, statusCode: 503);
    if (!node.IsOnline) return Results.Conflict(new { error = "Node is offline." });
    try
    {
        if (command.Level is { } value) await mqtt.PublishLevelAsync(node.DeviceId, index, value, token);
        else await mqtt.PublishCommandAsync(node.DeviceId, index, command.State!.Value, token);
        return Results.Accepted();
    }
    catch (Exception ex) when (ex is IOException or ObjectDisposedException)
    {
        return Results.Json(new { error = "MQTT publish failed." }, statusCode: 503);
    }
});

app.MapPost("/api/nodes/{id:int}/outputs/{index:int}/mode", async (
    int id, int index, OutputModeCommand command, HomeDb db, MqttGateway mqtt, CancellationToken token) =>
{
    if (command.Mode is not ("digital" or "pwm"))
        return Results.BadRequest(new { error = "Mode must be digital or pwm." });
    var node = await db.Nodes.Include(x => x.Outputs).FirstOrDefaultAsync(x => x.Id == id, token);
    var output = node?.Outputs.FirstOrDefault(x => x.Index == index);
    if (node is null || output is null) return Results.NotFound();
    if (await db.Rules.AnyAsync(x => x.Enabled && x.ActuatorNodeId == id && x.OutputIndex == index, token) ||
        await db.Schedules.AnyAsync(x => x.Enabled && x.ActuatorNodeId == id && x.OutputIndex == index, token) ||
        await db.Timers.AnyAsync(x => x.ActuatorNodeId == id && x.OutputIndex == index && x.CompletedAtUtc == null, token))
        return Results.Conflict(new { error = "Pause the automation before changing output mode." });
    if (!mqtt.IsConnected) return Results.Json(new { error = "MQTT broker is disconnected." }, statusCode: 503);
    if (!node.IsOnline) return Results.Conflict(new { error = "Node is offline." });
    try
    {
        await mqtt.PublishModeAsync(node.DeviceId, index, command.Mode, token);
        return Results.Accepted();
    }
    catch (Exception ex) when (ex is IOException or ObjectDisposedException)
    {
        return Results.Json(new { error = "MQTT publish failed." }, statusCode: 503);
    }
});

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));
app.MapAutomationEndpoints();
app.MapFallbackToFile("index.html");
app.Run();

static bool ValidColor(string? color) => color is { Length: 7 } && color[0] == '#' &&
    color.AsSpan(1).ToString().All(Uri.IsHexDigit);
static bool ValidKind(string? kind) => kind is "sensor" or "actuator";

public partial class Program;
