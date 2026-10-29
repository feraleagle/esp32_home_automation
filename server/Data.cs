using Microsoft.EntityFrameworkCore;

namespace HomeAutomation;

public sealed class HomeDb(DbContextOptions<HomeDb> options) : DbContext(options)
{
    public DbSet<Area> Areas => Set<Area>();
    public DbSet<Node> Nodes => Set<Node>();
    public DbSet<Output> Outputs => Set<Output>();
    public DbSet<SensorChannel> Sensors => Set<SensorChannel>();
    public DbSet<AutomationRule> Rules => Set<AutomationRule>();
    public DbSet<TimeSchedule> Schedules => Set<TimeSchedule>();
    public DbSet<CountdownTimer> Timers => Set<CountdownTimer>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Area>().HasIndex(x => x.Name).IsUnique();
        model.Entity<Node>().HasIndex(x => x.DeviceId).IsUnique();
        model.Entity<Output>().HasIndex(x => new { x.NodeId, x.Index }).IsUnique();
        model.Entity<SensorChannel>().HasIndex(x => new { x.NodeId, x.Key }).IsUnique();
        model.Entity<AutomationRule>().HasIndex(x => new { x.ActuatorNodeId, x.OutputIndex }).IsUnique();
        model.Entity<TimeSchedule>().HasIndex(x => new { x.ActuatorNodeId, x.OutputIndex }).IsUnique();
        model.Entity<Node>().HasOne(x => x.Area).WithMany().HasForeignKey(x => x.AreaId).OnDelete(DeleteBehavior.SetNull);
        model.Entity<Output>().HasOne(x => x.Node).WithMany(x => x.Outputs).HasForeignKey(x => x.NodeId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<SensorChannel>().HasOne(x => x.Node).WithMany(x => x.Sensors).HasForeignKey(x => x.NodeId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<AutomationRule>().HasOne<Node>().WithMany().HasForeignKey(x => x.SensorNodeId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<AutomationRule>().HasOne<Node>().WithMany().HasForeignKey(x => x.ActuatorNodeId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<TimeSchedule>().HasOne<Node>().WithMany().HasForeignKey(x => x.ActuatorNodeId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<CountdownTimer>().HasOne<Node>().WithMany().HasForeignKey(x => x.ActuatorNodeId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class Area
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Color { get; set; } = "#4968e9";
}

public sealed class Node
{
    public int Id { get; set; }
    public string DeviceId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "actuator";
    public int? AreaId { get; set; }
    public Area? Area { get; set; }
    public bool IsOnline { get; set; }
    public DateTime? LastSeenUtc { get; set; }
    public List<Output> Outputs { get; set; } = [];
    public List<SensorChannel> Sensors { get; set; } = [];
}

public sealed class Output
{
    public int Id { get; set; }
    public int NodeId { get; set; }
    public Node Node { get; set; } = null!;
    public int Index { get; set; }
    public int Gpio { get; set; }
    public bool IsOn { get; set; }
    public string Mode { get; set; } = "digital";
    public int Level { get; set; }
}

public sealed class SensorChannel
{
    public int Id { get; set; }
    public int NodeId { get; set; }
    public Node Node { get; set; } = null!;
    public string Key { get; set; } = "";
    public double Value { get; set; }
    public string Unit { get; set; } = "";
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class AutomationRule
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int SensorNodeId { get; set; }
    public string SensorKey { get; set; } = "";
    public string Operator { get; set; } = ">";
    public double Threshold { get; set; }
    public int ActuatorNodeId { get; set; }
    public int OutputIndex { get; set; }
    public bool ActionState { get; set; }
    public bool? ElseState { get; set; }
    public bool Enabled { get; set; } = true;
    public bool WasMatching { get; set; }
    public DateTime? LastTriggeredAtUtc { get; set; }
}

public sealed class TimeSchedule
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int ActuatorNodeId { get; set; }
    public int OutputIndex { get; set; }
    public int StartMinute { get; set; }
    public int EndMinute { get; set; }
    public int DaysMask { get; set; } = 127;
    public bool Enabled { get; set; } = true;
    public DateTime? LastCommandAtUtc { get; set; }
}

public sealed class CountdownTimer
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int ActuatorNodeId { get; set; }
    public int OutputIndex { get; set; }
    public int DurationMinutes { get; set; }
    public bool ActionState { get; set; } = true;
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? EndsAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
}

public sealed record AreaInput(string Name, string? Color);
public sealed record NodeInput(string DeviceId, string? Name, int? AreaId, string? Kind);
public sealed record NodeEdit(string Name, int? AreaId, string? Kind);
public sealed record OutputCommand(bool? State, int? Level);
public sealed record OutputModeCommand(string Mode);
public sealed record RuleInput(string Name, int SensorNodeId, string SensorKey, string Operator,
    double Threshold, int ActuatorNodeId, int OutputIndex, bool ActionState, bool? ElseState, bool Enabled);
public sealed record ScheduleInput(string Name, int ActuatorNodeId, int OutputIndex,
    int StartMinute, int EndMinute, int DaysMask, bool Enabled);
public sealed record TimerInput(string Name, int ActuatorNodeId, int OutputIndex,
    int DurationMinutes, bool ActionState);
