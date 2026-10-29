using Microsoft.EntityFrameworkCore;

namespace HomeAutomation;

public static class SchemaUpgrade
{
    // EnsureCreated handles a fresh database. These additive statements preserve
    // area/node data from the first version of this project.
    public static void Apply(HomeDb db)
    {
        db.Database.EnsureCreated();
        var connection = db.Database.GetDbConnection();
        connection.Open();
        try
        {
            using var columns = connection.CreateCommand();
            columns.CommandText = "PRAGMA table_info('Nodes')";
            using var reader = columns.ExecuteReader();
            var hasKind = false;
            while (reader.Read()) if (reader.GetString(1) == "Kind") hasKind = true;
            reader.Close();
            if (!hasKind) db.Database.ExecuteSqlRaw("ALTER TABLE Nodes ADD COLUMN Kind TEXT NOT NULL DEFAULT 'actuator'");

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS Outputs (
                    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    NodeId INTEGER NOT NULL,
                    "Index" INTEGER NOT NULL,
                    Gpio INTEGER NOT NULL,
                    IsOn INTEGER NOT NULL,
                    FOREIGN KEY (NodeId) REFERENCES Nodes(Id) ON DELETE CASCADE
                )
                """);
            db.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS IX_Outputs_NodeId_Index ON Outputs(NodeId, \"Index\")");

            using (var outputColumns = connection.CreateCommand())
            {
                outputColumns.CommandText = "PRAGMA table_info('Outputs')";
                using var outputReader = outputColumns.ExecuteReader();
                var hasMode = false;
                var hasLevel = false;
                while (outputReader.Read())
                {
                    if (outputReader.GetString(1) == "Mode") hasMode = true;
                    if (outputReader.GetString(1) == "Level") hasLevel = true;
                }
                outputReader.Close();
                if (!hasMode) db.Database.ExecuteSqlRaw("ALTER TABLE Outputs ADD COLUMN Mode TEXT NOT NULL DEFAULT 'digital'");
                if (!hasLevel) db.Database.ExecuteSqlRaw("ALTER TABLE Outputs ADD COLUMN Level INTEGER NOT NULL DEFAULT 0");
            }

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS Sensors (
                    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    NodeId INTEGER NOT NULL,
                    "Key" TEXT NOT NULL,
                    Value REAL NOT NULL,
                    Unit TEXT NOT NULL,
                    UpdatedAtUtc TEXT NOT NULL,
                    FOREIGN KEY (NodeId) REFERENCES Nodes(Id) ON DELETE CASCADE
                )
                """);
            db.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS IX_Sensors_NodeId_Key ON Sensors(NodeId, \"Key\")");

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS Rules (
                    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    Name TEXT NOT NULL,
                    SensorNodeId INTEGER NOT NULL,
                    SensorKey TEXT NOT NULL,
                    Operator TEXT NOT NULL,
                    Threshold REAL NOT NULL,
                    ActuatorNodeId INTEGER NOT NULL,
                    OutputIndex INTEGER NOT NULL,
                    ActionState INTEGER NOT NULL,
                    ElseState INTEGER NULL,
                    Enabled INTEGER NOT NULL,
                    WasMatching INTEGER NOT NULL,
                    LastTriggeredAtUtc TEXT NULL,
                    FOREIGN KEY (SensorNodeId) REFERENCES Nodes(Id) ON DELETE CASCADE,
                    FOREIGN KEY (ActuatorNodeId) REFERENCES Nodes(Id) ON DELETE CASCADE
                )
                """);
            db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_Rules_SensorNodeId ON Rules(SensorNodeId)");
            db.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS IX_Rules_ActuatorNodeId_OutputIndex ON Rules(ActuatorNodeId, OutputIndex)");

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS Schedules (
                    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    Name TEXT NOT NULL,
                    ActuatorNodeId INTEGER NOT NULL,
                    OutputIndex INTEGER NOT NULL,
                    StartMinute INTEGER NOT NULL,
                    EndMinute INTEGER NOT NULL,
                    DaysMask INTEGER NOT NULL,
                    Enabled INTEGER NOT NULL,
                    LastCommandAtUtc TEXT NULL,
                    FOREIGN KEY (ActuatorNodeId) REFERENCES Nodes(Id) ON DELETE CASCADE
                )
                """);
            db.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS IX_Schedules_ActuatorNodeId_OutputIndex ON Schedules(ActuatorNodeId, OutputIndex)");

            db.Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS Timers (
                    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    Name TEXT NOT NULL,
                    ActuatorNodeId INTEGER NOT NULL,
                    OutputIndex INTEGER NOT NULL,
                    DurationMinutes INTEGER NOT NULL,
                    ActionState INTEGER NOT NULL,
                    StartedAtUtc TEXT NULL,
                    EndsAtUtc TEXT NULL,
                    CompletedAtUtc TEXT NULL,
                    FOREIGN KEY (ActuatorNodeId) REFERENCES Nodes(Id) ON DELETE CASCADE
                )
                """);
            db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_Timers_ActuatorNodeId ON Timers(ActuatorNodeId)");
        }
        finally { connection.Close(); }
    }
}
