using Microsoft.EntityFrameworkCore;

namespace HomeAutomation;

public static class AutomationEndpoints
{
    public static void MapAutomationEndpoints(this WebApplication app)
    {
        app.MapPost("/api/rules", async (RuleInput input, HomeDb db) =>
        {
            var error = await ValidateRule(input, db, null);
            if (error is not null) return Results.BadRequest(new { error });
            var rule = new AutomationRule();
            Copy(input, rule);
            db.Rules.Add(rule);
            await db.SaveChangesAsync();
            return Results.Ok(new { rule.Id });
        });

        app.MapPut("/api/rules/{id:int}", async (int id, RuleInput input, HomeDb db) =>
        {
            var rule = await db.Rules.FindAsync(id);
            if (rule is null) return Results.NotFound();
            var error = await ValidateRule(input, db, id);
            if (error is not null) return Results.BadRequest(new { error });
            Copy(input, rule);
            rule.WasMatching = false;
            rule.LastTriggeredAtUtc = null;
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        app.MapDelete("/api/rules/{id:int}", async (int id, HomeDb db) =>
        {
            var rule = await db.Rules.FindAsync(id);
            if (rule is null) return Results.NotFound();
            db.Rules.Remove(rule);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        app.MapPost("/api/schedules", async (ScheduleInput input, HomeDb db) =>
        {
            var error = await ValidateSchedule(input, db, null);
            if (error is not null) return Results.BadRequest(new { error });
            var schedule = new TimeSchedule();
            Copy(input, schedule);
            db.Schedules.Add(schedule);
            await db.SaveChangesAsync();
            return Results.Ok(new { schedule.Id });
        });

        app.MapPut("/api/schedules/{id:int}", async (int id, ScheduleInput input, HomeDb db) =>
        {
            var schedule = await db.Schedules.FindAsync(id);
            if (schedule is null) return Results.NotFound();
            var error = await ValidateSchedule(input, db, id);
            if (error is not null) return Results.BadRequest(new { error });
            Copy(input, schedule);
            schedule.LastCommandAtUtc = null;
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        app.MapDelete("/api/schedules/{id:int}", async (int id, HomeDb db) =>
        {
            var schedule = await db.Schedules.FindAsync(id);
            if (schedule is null) return Results.NotFound();
            db.Schedules.Remove(schedule);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        app.MapPost("/api/timers", async (TimerInput input, HomeDb db) =>
        {
            if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Trim().Length > 80)
                return Results.BadRequest(new { error = "Timer name must be 1–80 characters." });
            if (input.DurationMinutes is < 1 or > 1440)
                return Results.BadRequest(new { error = "Timer duration must be 1–1440 minutes." });
            var actuator = await db.Nodes.Include(x => x.Outputs).FirstOrDefaultAsync(x => x.Id == input.ActuatorNodeId);
            if (actuator?.Kind != "actuator" || actuator.Outputs.All(x => x.Index != input.OutputIndex))
                return Results.BadRequest(new { error = "Choose an actuator output that has reported at least once." });
            if (await db.Rules.AnyAsync(x => x.ActuatorNodeId == input.ActuatorNodeId && x.OutputIndex == input.OutputIndex) ||
                await db.Schedules.AnyAsync(x => x.ActuatorNodeId == input.ActuatorNodeId && x.OutputIndex == input.OutputIndex) ||
                await db.Timers.AnyAsync(x => x.ActuatorNodeId == input.ActuatorNodeId && x.OutputIndex == input.OutputIndex && x.CompletedAtUtc == null))
                return Results.Conflict(new { error = "This output already has an automation." });
            var timer = new CountdownTimer
            {
                Name = input.Name.Trim(), ActuatorNodeId = input.ActuatorNodeId,
                OutputIndex = input.OutputIndex, DurationMinutes = input.DurationMinutes,
                ActionState = input.ActionState
            };
            db.Timers.Add(timer);
            await db.SaveChangesAsync();
            return Results.Ok(new { timer.Id });
        });

        app.MapDelete("/api/timers/{id:int}", async (int id, HomeDb db) =>
        {
            var timer = await db.Timers.FindAsync(id);
            if (timer is null) return Results.NotFound();
            if (timer.StartedAtUtc is null || timer.CompletedAtUtc is not null)
                db.Timers.Remove(timer);
            else timer.EndsAtUtc = DateTime.UtcNow; // worker sends the ending action when connected
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }

    private static async Task<string?> ValidateRule(RuleInput input, HomeDb db, int? currentId)
    {
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Trim().Length > 80) return "Rule name must be 1–80 characters.";
        if (string.IsNullOrWhiteSpace(input.SensorKey) || input.SensorKey.Length > 40 ||
            !input.SensorKey.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')) return "Choose a valid sensor key.";
        if (input.Operator is not (">" or ">=" or "<" or "<=" or "==" or "!=")) return "Choose a comparison operator.";
        if (!double.IsFinite(input.Threshold)) return "Threshold must be a finite number.";
        var sensor = await db.Nodes.Include(x => x.Sensors).FirstOrDefaultAsync(x => x.Id == input.SensorNodeId);
        if (sensor?.Kind != "sensor" || sensor.Sensors.All(x => x.Key != input.SensorKey)) return "Choose a sensor reading that has reported at least once.";
        var actuator = await db.Nodes.Include(x => x.Outputs).FirstOrDefaultAsync(x => x.Id == input.ActuatorNodeId);
        if (actuator?.Kind != "actuator" || actuator.Outputs.All(x => x.Index != input.OutputIndex)) return "Choose an actuator output that has reported at least once.";
        if (await db.Rules.AnyAsync(x => x.ActuatorNodeId == input.ActuatorNodeId && x.OutputIndex == input.OutputIndex &&
            (!currentId.HasValue || x.Id != currentId.Value)))
            return "This output already has a sensor rule.";
        if (await db.Schedules.AnyAsync(x => x.ActuatorNodeId == input.ActuatorNodeId && x.OutputIndex == input.OutputIndex))
            return "This output already has a time schedule.";
        if (await db.Timers.AnyAsync(x => x.ActuatorNodeId == input.ActuatorNodeId && x.OutputIndex == input.OutputIndex && x.CompletedAtUtc == null))
            return "This output already has an active timer.";
        return null;
    }

    private static async Task<string?> ValidateSchedule(ScheduleInput input, HomeDb db, int? currentId)
    {
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Trim().Length > 80) return "Schedule name must be 1–80 characters.";
        if (input.StartMinute is < 0 or > 1439 || input.EndMinute is < 0 or > 1439 || input.StartMinute == input.EndMinute)
            return "Choose different start and end times.";
        if (input.DaysMask is < 1 or > 127) return "Choose at least one day.";
        var actuator = await db.Nodes.Include(x => x.Outputs).FirstOrDefaultAsync(x => x.Id == input.ActuatorNodeId);
        if (actuator?.Kind != "actuator" || actuator.Outputs.All(x => x.Index != input.OutputIndex)) return "Choose an actuator output that has reported at least once.";
        if (await db.Schedules.AnyAsync(x => x.ActuatorNodeId == input.ActuatorNodeId && x.OutputIndex == input.OutputIndex &&
            (!currentId.HasValue || x.Id != currentId.Value)))
            return "This output already has a time schedule.";
        if (await db.Rules.AnyAsync(x => x.ActuatorNodeId == input.ActuatorNodeId && x.OutputIndex == input.OutputIndex))
            return "This output already has a sensor rule.";
        if (await db.Timers.AnyAsync(x => x.ActuatorNodeId == input.ActuatorNodeId && x.OutputIndex == input.OutputIndex && x.CompletedAtUtc == null))
            return "This output already has an active timer.";
        return null;
    }

    private static void Copy(RuleInput input, AutomationRule rule)
    {
        rule.Name = input.Name.Trim();
        rule.SensorNodeId = input.SensorNodeId;
        rule.SensorKey = input.SensorKey;
        rule.Operator = input.Operator;
        rule.Threshold = input.Threshold;
        rule.ActuatorNodeId = input.ActuatorNodeId;
        rule.OutputIndex = input.OutputIndex;
        rule.ActionState = input.ActionState;
        rule.ElseState = input.ElseState;
        rule.Enabled = input.Enabled;
    }

    private static void Copy(ScheduleInput input, TimeSchedule schedule)
    {
        schedule.Name = input.Name.Trim();
        schedule.ActuatorNodeId = input.ActuatorNodeId;
        schedule.OutputIndex = input.OutputIndex;
        schedule.StartMinute = input.StartMinute;
        schedule.EndMinute = input.EndMinute;
        schedule.DaysMask = input.DaysMask;
        schedule.Enabled = input.Enabled;
    }
}
