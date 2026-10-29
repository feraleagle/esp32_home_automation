using Microsoft.EntityFrameworkCore;

namespace HomeAutomation;

public sealed class AutomationService(
    IDbContextFactory<HomeDb> dbFactory,
    MqttGateway mqtt,
    IConfiguration configuration,
    ILogger<AutomationService> logger) : BackgroundService
{
    public TimeZoneInfo TimeZone { get; } = TimeZoneInfo.FindSystemTimeZoneById(
        configuration["Automation:TimeZone"] ?? "Asia/Karachi");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ApplyTimersAsync(DateTimeOffset.UtcNow, stoppingToken);
                await ApplySchedulesAsync(DateTimeOffset.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Schedule check failed"); }
            try { if (!await timer.WaitForNextTickAsync(stoppingToken)) break; }
            catch (OperationCanceledException) { break; }
        }
    }

    public async Task ProcessSensorAsync(int sensorNodeId, string key, double value, CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var rules = await db.Rules.Where(x => x.Enabled && x.SensorNodeId == sensorNodeId && x.SensorKey == key)
            .ToListAsync(token);
        if (rules.Count == 0) return;
        var nodeIds = rules.Select(x => x.ActuatorNodeId).Distinct().ToList();
        var nodes = await db.Nodes.Include(x => x.Outputs)
            .Where(x => nodeIds.Contains(x.Id)).ToListAsync(token);

        foreach (var rule in rules)
        {
            var matching = Matches(rule.Operator, value, rule.Threshold);
            if (matching == rule.WasMatching && rule.LastTriggeredAtUtc is not null) continue;
            var desired = matching ? rule.ActionState : rule.ElseState;
            if (desired is null)
            {
                rule.WasMatching = matching;
                continue;
            }
            var node = nodes.FirstOrDefault(x => x.Id == rule.ActuatorNodeId);
            var output = node?.Outputs.FirstOrDefault(x => x.Index == rule.OutputIndex);
            if (node is null || output is null || !node.IsOnline || !mqtt.IsConnected) continue;
            try
            {
                if (output.IsOn != desired.Value)
                    await mqtt.PublishCommandAsync(node.DeviceId, rule.OutputIndex, desired.Value, token);
                rule.WasMatching = matching;
                rule.LastTriggeredAtUtc = DateTime.UtcNow;
                logger.LogInformation("Rule {Rule} set {Device} output {Output} to {State}",
                    rule.Name, node.DeviceId, rule.OutputIndex, desired.Value);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                logger.LogWarning("Rule {Rule} command failed: {Message}", rule.Name, ex.Message);
            }
        }
        await db.SaveChangesAsync(token);
    }

    public async Task ApplySchedulesAsync(DateTimeOffset nowUtc, CancellationToken token)
    {
        if (!mqtt.IsConnected) return;
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var schedules = await db.Schedules.Where(x => x.Enabled).ToListAsync(token);
        if (schedules.Count == 0) return;
        var nodeIds = schedules.Select(x => x.ActuatorNodeId).Distinct().ToList();
        var nodes = await db.Nodes.Include(x => x.Outputs).Where(x => nodeIds.Contains(x.Id)).ToListAsync(token);
        foreach (var schedule in schedules)
        {
            var node = nodes.FirstOrDefault(x => x.Id == schedule.ActuatorNodeId);
            var output = node?.Outputs.FirstOrDefault(x => x.Index == schedule.OutputIndex);
            if (node is null || output is null || !node.IsOnline) continue;
            var shouldBeOn = IsActive(schedule, nowUtc, TimeZone);
            if (output.IsOn == shouldBeOn) continue;
            if (schedule.LastCommandAtUtc is { } last && nowUtc.UtcDateTime - last < TimeSpan.FromMinutes(1)) continue;
            try
            {
                await mqtt.PublishCommandAsync(node.DeviceId, schedule.OutputIndex, shouldBeOn, token);
                schedule.LastCommandAtUtc = nowUtc.UtcDateTime;
                logger.LogInformation("Schedule {Schedule} set {Device} output {Output} to {State}",
                    schedule.Name, node.DeviceId, schedule.OutputIndex, shouldBeOn);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                logger.LogWarning("Schedule {Schedule} command failed: {Message}", schedule.Name, ex.Message);
            }
        }
        await db.SaveChangesAsync(token);
    }

    public async Task ApplyTimersAsync(DateTimeOffset nowUtc, CancellationToken token)
    {
        if (!mqtt.IsConnected) return;
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var timers = await db.Timers.Where(x => x.CompletedAtUtc == null).ToListAsync(token);
        if (timers.Count == 0) return;
        var nodeIds = timers.Select(x => x.ActuatorNodeId).Distinct().ToList();
        var nodes = await db.Nodes.Include(x => x.Outputs).Where(x => nodeIds.Contains(x.Id)).ToListAsync(token);
        foreach (var timer in timers)
        {
            var node = nodes.FirstOrDefault(x => x.Id == timer.ActuatorNodeId);
            var output = node?.Outputs.FirstOrDefault(x => x.Index == timer.OutputIndex);
            if (node is null || output is null || !node.IsOnline) continue;
            var starting = timer.StartedAtUtc is null;
            if (!starting && timer.EndsAtUtc > nowUtc.UtcDateTime) continue;
            var desired = starting ? timer.ActionState : !timer.ActionState;
            try
            {
                await mqtt.PublishCommandAsync(node.DeviceId, timer.OutputIndex, desired, token);
                if (starting)
                {
                    timer.StartedAtUtc = nowUtc.UtcDateTime;
                    timer.EndsAtUtc = nowUtc.UtcDateTime.AddMinutes(timer.DurationMinutes);
                }
                else timer.CompletedAtUtc = nowUtc.UtcDateTime;
                logger.LogInformation("Timer {Timer} set {Device} output {Output} to {State}",
                    timer.Name, node.DeviceId, timer.OutputIndex, desired);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                logger.LogWarning("Timer {Timer} command failed: {Message}", timer.Name, ex.Message);
            }
        }
        await db.SaveChangesAsync(token);
    }

    public static bool Matches(string op, double value, double threshold) => op switch
    {
        ">" => value > threshold,
        ">=" => value >= threshold,
        "<" => value < threshold,
        "<=" => value <= threshold,
        "==" => value == threshold,
        "!=" => value != threshold,
        _ => false
    };

    public static bool IsActive(TimeSchedule schedule, DateTimeOffset nowUtc, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(nowUtc, zone);
        var minute = local.Hour * 60 + local.Minute;
        var day = ((int)local.DayOfWeek + 6) % 7; // Monday = bit 0
        bool selected(int d) => (schedule.DaysMask & (1 << d)) != 0;
        if (schedule.StartMinute < schedule.EndMinute)
            return selected(day) && minute >= schedule.StartMinute && minute < schedule.EndMinute;
        var previousDay = (day + 6) % 7;
        return (selected(day) && minute >= schedule.StartMinute) ||
               (selected(previousDay) && minute < schedule.EndMinute);
    }
}
