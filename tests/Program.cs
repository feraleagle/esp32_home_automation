using HomeAutomation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Karachi");
var daytime = new TimeSchedule { StartMinute = 8 * 60, EndMinute = 16 * 60, DaysMask = 1 }; // Monday
static DateTimeOffset At(int day, int hour, int minute) => new(2026, 10, day, hour, minute, 0, TimeSpan.FromHours(5));
Check(!AutomationService.IsActive(daytime, At(26, 7, 59), zone), "before daytime window");
Check(AutomationService.IsActive(daytime, At(26, 8, 0), zone), "start of daytime window");
Check(AutomationService.IsActive(daytime, At(26, 15, 59), zone), "inside daytime window");
Check(!AutomationService.IsActive(daytime, At(26, 16, 0), zone), "end of daytime window");
Check(!AutomationService.IsActive(daytime, At(27, 10, 0), zone), "unselected weekday");

var overnight = new TimeSchedule { StartMinute = 22 * 60, EndMinute = 6 * 60, DaysMask = 1 };
Check(AutomationService.IsActive(overnight, At(26, 22, 0), zone), "overnight start");
Check(AutomationService.IsActive(overnight, At(27, 2, 0), zone), "overnight after midnight");
Check(!AutomationService.IsActive(overnight, At(27, 6, 0), zone), "overnight end");
Check(!AutomationService.IsActive(overnight, At(28, 2, 0), zone), "following night unselected");
Check(AutomationService.Matches(">", 30.1, 30), "greater-than rule");
Check(!AutomationService.Matches(">", 30, 30), "strict threshold");
Check(AutomationService.Matches("==", 1, 1), "boolean sensor as numeric rule");

var file = Path.Combine(Path.GetTempPath(), $"home-automation-check-{Guid.NewGuid():N}.db");
try
{
    using (var connection = new SqliteConnection($"Data Source={file}"))
    {
        connection.Open();
        using var create = connection.CreateCommand();
        create.CommandText = """
            CREATE TABLE Nodes (Id INTEGER PRIMARY KEY, DeviceId TEXT NOT NULL, Name TEXT NOT NULL,
                AreaId INTEGER NULL, IsOnline INTEGER NOT NULL, LastSeenUtc TEXT NULL);
            INSERT INTO Nodes (Id, DeviceId, Name, IsOnline) VALUES (1, 'esp32-test', 'Existing node', 0);
            CREATE TABLE Outputs (Id INTEGER PRIMARY KEY, NodeId INTEGER NOT NULL, "Index" INTEGER NOT NULL,
                Gpio INTEGER NOT NULL, IsOn INTEGER NOT NULL);
            INSERT INTO Outputs (Id, NodeId, "Index", Gpio, IsOn) VALUES (1, 1, 0, 4, 1);
            """;
        create.ExecuteNonQuery();
    }

    var options = new DbContextOptionsBuilder<HomeDb>().UseSqlite($"Data Source={file}").Options;
    using var db = new HomeDb(options);
    SchemaUpgrade.Apply(db);
    SchemaUpgrade.Apply(db);
    var node = db.Nodes.Single(x => x.Id == 1);
    Check(node.Kind == "actuator", "existing node preserved with actuator type");
    var output = db.Outputs.Single(x => x.NodeId == 1);
    Check(output.Mode == "digital" && output.Level == 0 && output.IsOn, "existing output gains PWM fields");
    Check(db.Rules.Count() == 0 && db.Schedules.Count() == 0 && db.Sensors.Count() == 0 && db.Timers.Count() == 0,
        "new automation tables available");
}
finally { File.Delete(file); }

Console.WriteLine("Automation checks passed.");
