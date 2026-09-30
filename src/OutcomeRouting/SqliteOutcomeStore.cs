using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace OutcomeRouting;

public interface IOutcomeStore
{
    void Begin(RoutingTask task, string catalogRevision);
    IReadOnlyList<Evidence> ReadEvidence(string cohort, string catalogRevision, PolicySettings settings);
    void RecordDecision(Guid runId, DecisionSnapshot decision);
    void RecordAttempt(Guid runId, AttemptRecord attempt);
    void Finish(Guid runId, RunStatus status, string? actualRouteIdentity, Feedback? feedback);
    bool ReportFeedback(Guid runId, string actualRouteIdentity, Feedback feedback);
    RunRecord GetRun(Guid runId);
    IReadOnlyList<AttemptRecord> GetAttempts(Guid runId);
}

/// <summary>Short-lived connections and SQLite transactions also coordinate independent store instances/processes.</summary>
public sealed class SqliteOutcomeStore : IOutcomeStore
{
    private readonly string _connectionString;
    private readonly TimeProvider _clock;
    public string Path { get; }

    public SqliteOutcomeStore(string path, TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        _clock = clock ?? TimeProvider.System;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path,
            ForeignKeys = true,
            DefaultTimeout = 30,
            Pooling = false
        }.ToString();

        using var connection = Open();
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL;";
            pragma.ExecuteScalar();
        }

        using var transaction = connection.BeginTransaction();
        using var versionCommand = Command(connection, transaction, "PRAGMA user_version;");
        long version = (long)versionCommand.ExecuteScalar()!;

        if (version == 0)
        {
            using var tables = Command(
                connection,
                transaction,
                "SELECT count(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';");
            if ((long)tables.ExecuteScalar()! != 0)
            {
                throw new InvalidDataException("Unversioned nonempty routing database; refusing to replace it.");
            }

            using var schema = Command(
                connection,
                transaction,
                """
                CREATE TABLE runs(
                  sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                  run_id TEXT NOT NULL UNIQUE,
                  cohort TEXT NOT NULL,
                  catalog_revision TEXT NOT NULL,
                  created_ms INTEGER NOT NULL,
                  finished_ms INTEGER,
                  status TEXT NOT NULL CHECK(status IN ('Running','Completed','Failed','Cancelled','Abandoned')),
                  actual_route TEXT,
                  decision_json TEXT,
                  CHECK((status='Running')=(finished_ms IS NULL)),
                  CHECK((status='Completed')=(actual_route IS NOT NULL))
                );
                CREATE TABLE attempts(
                  run_id TEXT NOT NULL REFERENCES runs(run_id),
                  ordinal INTEGER NOT NULL CHECK(ordinal>0),
                  route_name TEXT NOT NULL,
                  route_identity TEXT NOT NULL,
                  tier INTEGER NOT NULL CHECK(tier BETWEEN 0 AND 2),
                  duration_ticks INTEGER NOT NULL CHECK(duration_ticks>=0),
                  error_type TEXT,
                  completed INTEGER NOT NULL CHECK(completed IN (0,1)),
                  committed INTEGER NOT NULL CHECK(committed IN (0,1)),
                  first_update_ticks INTEGER CHECK(first_update_ticks>=0),
                  PRIMARY KEY(run_id,ordinal),
                  UNIQUE(run_id,route_identity),
                  CHECK(completed=0 OR error_type IS NULL)
                );
                CREATE TABLE feedback(
                  run_id TEXT PRIMARY KEY REFERENCES runs(run_id),
                  route_identity TEXT NOT NULL,
                  outcome TEXT NOT NULL CHECK(outcome IN ('Unknown','Success','Failure')),
                  provenance TEXT NOT NULL CHECK(provenance IN ('None','Verifier','Application')),
                  source TEXT NOT NULL,
                  CHECK((outcome='Unknown')=(provenance='None'))
                );
                CREATE INDEX evidence_lookup ON runs(cohort,catalog_revision,finished_ms);
                PRAGMA user_version=1;
                """);
            schema.ExecuteNonQuery();
        }
        else if (version != 1)
        {
            throw new InvalidDataException($"Unsupported routing schema version {version}.");
        }

        using var integrity = Command(connection, transaction, "PRAGMA quick_check;");
        if (!string.Equals(integrity.ExecuteScalar() as string, "ok", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Routing database integrity check failed.");
        }

        // A known version without its tables/columns is corruption, not an empty history.
        using var shape = Command(
            connection,
            transaction,
            """
            SELECT r.run_id,r.decision_json,a.ordinal,f.provenance FROM runs r
            LEFT JOIN attempts a ON a.run_id=r.run_id LEFT JOIN feedback f ON f.run_id=r.run_id LIMIT 0;
            """);
        shape.ExecuteNonQuery();
        transaction.Commit();
    }

    public void Begin(RoutingTask task, string catalogRevision)
    {
        Guard.Key(catalogRevision, nameof(catalogRevision));
        using var connection = Open();
        using var command = Command(
            connection,
            null,
            """
            INSERT INTO runs(run_id,cohort,catalog_revision,created_ms,status)
            VALUES($id,$cohort,$revision,$now,'Running');
            """,
            ("$id", Key(task.RunId)),
            ("$cohort", task.Cohort),
            ("$revision", catalogRevision),
            ("$now", Now));
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<Evidence> ReadEvidence(string cohort, string catalogRevision, PolicySettings settings)
    {
        Guard.Key(cohort, nameof(cohort));
        Guard.Key(catalogRevision, nameof(catalogRevision));
        settings.Validate();
        using var connection = Open();
        using var command = Command(
            connection,
            null,
            """
            SELECT a.route_name,a.route_identity,a.tier,f.outcome,f.provenance
            FROM runs r JOIN feedback f ON f.run_id=r.run_id AND f.route_identity=r.actual_route
            JOIN attempts a ON a.run_id=r.run_id AND a.route_identity=r.actual_route
            WHERE r.cohort=$cohort AND r.catalog_revision=$revision AND r.status='Completed'
              AND r.finished_ms >= $cutoff AND a.completed=1 AND a.error_type IS NULL
              AND f.outcome IN ('Success','Failure') AND f.provenance IN ('Verifier','Application')
            ORDER BY r.finished_ms DESC,r.sequence DESC LIMIT $limit;
            """,
            ("$cohort", cohort),
            ("$revision", catalogRevision),
            ("$cutoff", Now - (long)settings.EvidenceAge.TotalMilliseconds),
            ("$limit", settings.EvidenceLimit));
        using var reader = command.ExecuteReader();
        List<Evidence> evidence = [];

        while (reader.Read())
        {
            evidence.Add(new Evidence(
                reader.GetString(0),
                reader.GetString(1),
                (Tier)reader.GetInt32(2),
                Enum.Parse<Outcome>(reader.GetString(3)),
                Enum.Parse<Provenance>(reader.GetString(4))));
        }

        return evidence.AsReadOnly();
    }

    public void RecordDecision(Guid runId, DecisionSnapshot decision)
    {
        // The caller-visible projection contains the task; deliberately do not persist it.
        string json = JsonSerializer.Serialize(new
        {
            decision.Model,
            decision.Recommended,
            decision.Probabilities,
            decision.Selected,
            decision.Reasons
        });

        using var connection = Open();
        using var command = Command(
            connection,
            null,
            """
            UPDATE runs SET decision_json=$json WHERE run_id=$id AND status='Running' AND decision_json IS NULL;
            """,
            ("$id", Key(runId)),
            ("$json", json));
        RequireOne(command.ExecuteNonQuery(), "Decision requires an existing running run without a decision.");
    }

    public void RecordAttempt(Guid runId, AttemptRecord attempt)
    {
        Guard.Key(attempt.Route, nameof(attempt.Route), 24);
        Guard.Key(attempt.RouteIdentity, nameof(attempt.RouteIdentity));
        if (!Enum.IsDefined(attempt.Tier) || attempt.Number <= 0 || attempt.DurationTicks < 0 ||
            attempt.FirstUpdateTicks < 0 || (attempt.ResponseCompleted && attempt.ErrorType is not null))
        {
            throw new ArgumentException("Invalid attempt telemetry.", nameof(attempt));
        }

        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var check = Command(
            connection,
            transaction,
            """
            SELECT count(*) FROM runs WHERE run_id=$id AND status='Running' AND decision_json IS NOT NULL
              AND $ordinal=(SELECT count(*)+1 FROM attempts WHERE run_id=$id);
            """,
            ("$id", Key(runId)),
            ("$ordinal", attempt.Number));
        RequireOne(Convert.ToInt32(check.ExecuteScalar()), "Attempt requires a running routed run and the next ordinal.");

        using var insert = Command(
            connection,
            transaction,
            """
            INSERT INTO attempts VALUES($id,$ordinal,$name,$route,$tier,$duration,$error,$completed,$committed,$first);
            """,
            ("$id", Key(runId)),
            ("$ordinal", attempt.Number),
            ("$name", attempt.Route),
            ("$route", attempt.RouteIdentity),
            ("$tier", (int)attempt.Tier),
            ("$duration", attempt.DurationTicks),
            ("$error", attempt.ErrorType),
            ("$completed", attempt.ResponseCompleted ? 1 : 0),
            ("$committed", attempt.OutputCommitted ? 1 : 0),
            ("$first", attempt.FirstUpdateTicks));
        insert.ExecuteNonQuery();
        transaction.Commit();
    }

    public void Finish(Guid runId, RunStatus status, string? actualRouteIdentity, Feedback? feedback)
    {
        if (!Enum.IsDefined(status) || status == RunStatus.Running ||
            (status == RunStatus.Completed) != (actualRouteIdentity is not null) ||
            (status != RunStatus.Completed && feedback is not null))
        {
            throw new ArgumentException("Only a completed response can receive feedback and an actual route.");
        }

        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        if (actualRouteIdentity is not null)
        {
            using var actual = Command(
                connection,
                transaction,
                """
                SELECT count(*) FROM attempts WHERE run_id=$id AND route_identity=$route AND completed=1 AND error_type IS NULL;
                """,
                ("$id", Key(runId)),
                ("$route", actualRouteIdentity));
            RequireOne(Convert.ToInt32(actual.ExecuteScalar()), "Actual route must have a stored completed attempt.");
        }

        using var finish = Command(
            connection,
            transaction,
            """
            UPDATE runs SET status=$status,finished_ms=$now,actual_route=$route WHERE run_id=$id AND status='Running';
            """,
            ("$id", Key(runId)),
            ("$status", status.ToString()),
            ("$now", Now),
            ("$route", actualRouteIdentity));
        RequireOne(finish.ExecuteNonQuery(), "Run is unknown or already terminal.");

        if (feedback is not null)
        {
            InsertFeedback(connection, transaction, runId, actualRouteIdentity!, feedback);
        }

        transaction.Commit();
    }

    public bool ReportFeedback(Guid runId, string actualRouteIdentity, Feedback feedback)
    {
        ArgumentNullException.ThrowIfNull(feedback);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var check = Command(
            connection,
            transaction,
            """
            SELECT count(*) FROM runs WHERE run_id=$id AND status='Completed' AND actual_route=$route;
            """,
            ("$id", Key(runId)),
            ("$route", actualRouteIdentity));
        RequireOne(Convert.ToInt32(check.ExecuteScalar()), "Feedback requires a local completed run and its actual route.");
        var existing = ReadFeedback(connection, transaction, runId);

        if (existing is not null)
        {
            if (existing != feedback)
            {
                throw new InvalidOperationException("Conflicting terminal feedback is not allowed.");
            }

            transaction.Commit();
            return false;
        }

        InsertFeedback(connection, transaction, runId, actualRouteIdentity, feedback);
        transaction.Commit();
        return true;
    }

    public RunRecord GetRun(Guid runId)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = Command(
            connection,
            transaction,
            """
            SELECT cohort,catalog_revision,status,actual_route FROM runs WHERE run_id=$id;
            """,
            ("$id", Key(runId)));
        RunRecord run;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read())
            {
                throw new KeyNotFoundException("Unknown local run ID.");
            }

            run = new RunRecord(
                runId,
                reader.GetString(0),
                reader.GetString(1),
                Enum.Parse<RunStatus>(reader.GetString(2)),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                null);
        }

        run = run with { Feedback = ReadFeedback(connection, transaction, runId) };
        transaction.Commit();
        return run;
    }

    public IReadOnlyList<AttemptRecord> GetAttempts(Guid runId)
    {
        using var connection = Open();
        using var command = Command(
            connection,
            null,
            """
            SELECT ordinal,route_name,route_identity,tier,duration_ticks,error_type,completed,committed,first_update_ticks
            FROM attempts WHERE run_id=$id ORDER BY ordinal;
            """,
            ("$id", Key(runId)));
        using var reader = command.ExecuteReader();
        List<AttemptRecord> attempts = [];

        while (reader.Read())
        {
            attempts.Add(new AttemptRecord(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                (Tier)reader.GetInt32(3),
                reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetBoolean(6),
                reader.GetBoolean(7),
                reader.IsDBNull(8) ? null : reader.GetInt64(8)));
        }

        return attempts.AsReadOnly();
    }

    private static Feedback? ReadFeedback(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid runId)
    {
        using var command = Command(
            connection,
            transaction,
            "SELECT outcome,provenance,source FROM feedback WHERE run_id=$id;",
            ("$id", Key(runId)));

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new Feedback(
            Enum.Parse<Outcome>(reader.GetString(0)),
            Enum.Parse<Provenance>(reader.GetString(1)),
            reader.GetString(2));
    }

    private static void InsertFeedback(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid runId,
        string route,
        Feedback feedback)
    {
        using var command = Command(
            connection,
            transaction,
            "INSERT INTO feedback VALUES($id,$route,$outcome,$provenance,$source);",
            ("$id", Key(runId)),
            ("$route", route),
            ("$outcome", feedback.Outcome.ToString()),
            ("$provenance", feedback.Provenance.ToString()),
            ("$source", feedback.Source));
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);

        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private long Now => _clock.GetUtcNow().ToUnixTimeMilliseconds();

    private static string Key(Guid id)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Empty run ID.");
        }

        return id.ToString("D");
    }

    private static void RequireOne(int count, string message)
    {
        if (count != 1)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static SqliteCommand Command(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }
}
